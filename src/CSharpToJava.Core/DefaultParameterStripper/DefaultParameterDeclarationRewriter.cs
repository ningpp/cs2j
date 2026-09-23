using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// Strips default parameter values from every kind of callable declaration and
/// rewrites every call site so that it passes all arguments explicitly.
///
/// Declarations handled
///   methods (incl. generic, async, static, virtual/abstract/override,
///            explicit-interface, partial, expression bodied),
///   constructors (incl. struct, expression bodied, with initializers),
///   local functions,
///   delegates,
///   record / class / struct primary constructors.
///
/// Call sites handled
///   invocations (incl. extension methods, delegate invocations, conditional
///                access and base calls),
///   object creation (explicit and target-typed <c>new()</c>),
///   constructor initializers (<c>: base(...)</c> / <c>: this(...)</c>),
///   primary-constructor base calls,
///   attribute applications.
///
/// Two mechanisms keep the rewritten project compilable:
///   1. Call-site completion inserts the declared default expression as a
///      NAMED argument (<c>M(1, b: 2)</c>). Because the argument is named, the
///      binding cannot drift to an unrelated overload that merely happens to
///      accept the extra positional value.
///   2. Forwarding overloads are still generated so that callers which cannot
///      be completed (unresolved symbols, method-group conversions, implicit
///      <c>base()</c> calls) keep working.
/// </summary>
internal sealed class DefaultParameterDeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _model;
    private readonly DefaultParameterStripperOptions _options;
    private readonly DefaultParameterStripperStatistics _stats;
    private readonly List<DefaultParameterStripDiagnostic> _diagnostics;
    private readonly DefaultParameterCatalog _catalog;
    private readonly bool _hasDefaults;

    private HashSet<string> _typeSignatures = new(StringComparer.Ordinal);
    private HashSet<string> _localFunctionSignatures = new(StringComparer.Ordinal);

    // Tracks whether the current type already has a [JsonConstructor]-attributed
    // constructor so that at most one is emitted after strip-default.
    private bool _typeHasJsonConstructor;
    private bool _typeAddedJsonConstructor;

    public bool Changed { get; private set; }

    public DefaultParameterDeclarationRewriter(
        SemanticModel? model,
        DefaultParameterStripperOptions options,
        DefaultParameterStripperStatistics stats,
        List<DefaultParameterStripDiagnostic> diagnostics,
        DefaultParameterCatalog catalog,
        bool hasDefaults = true)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _catalog = catalog;
        _hasDefaults = hasDefaults;
    }

    // -----------------------------------------------------------------------
    // Type declarations
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (!HasDirtyMembers(node.Members) && !HasPrimaryDefaults(node))
        {
            return base.VisitClassDeclaration(node);
        }
        return VisitTypeDeclarationCore(node, (n, members) => n.WithMembers(members));
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        if (!HasDirtyMembers(node.Members) && !HasPrimaryDefaults(node))
        {
            return base.VisitStructDeclaration(node);
        }
        return VisitTypeDeclarationCore(node, (n, members) => n.WithMembers(members));
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        if (!HasDirtyMembers(node.Members))
        {
            return base.VisitInterfaceDeclaration(node);
        }
        return VisitTypeDeclarationCore(node, (n, members) => n.WithMembers(members));
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        if (!HasDirtyMembers(node.Members) && !HasPrimaryDefaults(node))
        {
            return base.VisitRecordDeclaration(node);
        }
        return VisitTypeDeclarationCore(node, (n, members) => n.WithMembers(members));
    }

    private SyntaxNode? VisitTypeDeclarationCore<T>(
        T node,
        Func<T, SyntaxList<MemberDeclarationSyntax>, T> withMembers)
        where T : TypeDeclarationSyntax
    {
        var savedTypeSignatures = _typeSignatures;
        var savedLocalSignatures = _localFunctionSignatures;
        var savedHasJsonConstructor = _typeHasJsonConstructor;
        var savedAddedJsonConstructor = _typeAddedJsonConstructor;
        _typeSignatures = new HashSet<string>(StringComparer.Ordinal);
        _localFunctionSignatures = new HashSet<string>(StringComparer.Ordinal);
        _typeHasJsonConstructor = TypeHasJsonConstructor(node.Members);
        _typeAddedJsonConstructor = false;

        foreach (var member in node.Members)
        {
            RegisterTypeMemberSignature(member);
        }

        var newMembers = new List<MemberDeclarationSyntax>();
        var isInterface = node is InterfaceDeclarationSyntax;
        var isStruct = node is StructDeclarationSyntax;

        foreach (var member in node.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    ProcessMethod(method, newMembers, isInterface);
                    break;
                case ConstructorDeclarationSyntax ctor:
                    ProcessConstructor(ctor, newMembers, isStruct);
                    break;
                default:
                    if (Visit(member) is MemberDeclarationSyntax visited)
                    {
                        newMembers.Add(visited);
                    }
                    break;
            }
        }

        var result = withMembers(node, SyntaxFactory.List(newMembers));

        if (node.BaseList != null && Visit(node.BaseList) is BaseListSyntax visitedBase)
        {
            result = (T)result.WithBaseList(visitedBase);
        }

        if (node.AttributeLists.Count > 0)
        {
            result = (T)result.WithAttributeLists(VisitList(node.AttributeLists));
        }

        result = ProcessPrimaryConstructor(result, isStruct);

        _typeSignatures = savedTypeSignatures;
        _localFunctionSignatures = savedLocalSignatures;
        _typeHasJsonConstructor = savedHasJsonConstructor;
        _typeAddedJsonConstructor = savedAddedJsonConstructor;
        return result;
    }

    /// <summary>
    /// Strips defaults from a record / class / struct primary constructor and
    /// appends the equivalent forwarding constructors.
    /// </summary>
    private T ProcessPrimaryConstructor<T>(T node, bool isStruct) where T : TypeDeclarationSyntax
    {
        var parameterList = node.ParameterList;
        if (parameterList == null || !HasDefaults(parameterList.Parameters))
        {
            return node;
        }

        if (HasCallerAttribute(parameterList))
        {
            _stats.SkippedUnsupported++;
            return node;
        }

        Changed = true;
        _stats.DeclarationsStripped++;
        CountStrippedParameters(parameterList.Parameters);

        var stripped = (T)node.WithParameterList(StripDefaults(parameterList));

        if (!_options.GenerateOverloads || _model == null)
        {
            return stripped;
        }

        var members = new List<MemberDeclarationSyntax>(stripped.Members);
        foreach (var overload in DefaultParameterOverloadBuilder.BuildPrimaryConstructorOverloads(
                     node, _model, _stats, isStruct))
        {
            if (!TryRegisterTypeSignature(overload))
            {
                _stats.SkippedConflict++;
                continue;
            }
            members.Add(overload);
        }

        return (T)stripped.WithMembers(SyntaxFactory.List(members));
    }

    // -----------------------------------------------------------------------
    // Methods
    // -----------------------------------------------------------------------

    private void ProcessMethod(
        MethodDeclarationSyntax method,
        List<MemberDeclarationSyntax> output,
        bool isInterface)
    {
        var visited = base.VisitMethodDeclaration(method) as MethodDeclarationSyntax ?? method;

        if (!HasDefaults(method.ParameterList.Parameters))
        {
            output.Add(visited);
            return;
        }

        // Caller-attributed parameters MUST keep their default (CS4020/CS4022).
        if (HasCallerAttribute(method.ParameterList))
        {
            _stats.SkippedUnsupported++;
            output.Add(visited);
            return;
        }

        // xUnit/NUnit test methods: InlineData supplies values positionally, so
        // stripping a default would desynchronise attribute and signature.
        if (IsTestMethod(method))
        {
            _stats.SkippedUnsupported++;
            output.Add(visited);
            return;
        }

        // Explicit interface implementations and overrides only re-declare the
        // defaults of the member they implement; the forwarding overload lives
        // on the base member.
        var stripOnly = method.ExplicitInterfaceSpecifier != null
            || method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword));

        Changed = true;
        _stats.DeclarationsStripped++;
        CountStrippedParameters(method.ParameterList.Parameters);

        output.Add(visited.WithParameterList(StripDefaults(method.ParameterList)));

        if (stripOnly || !_options.GenerateOverloads || _model == null)
        {
            return;
        }

        foreach (var overload in DefaultParameterOverloadBuilder.BuildOverloads(
                     method, _model, _stats, _catalog))
        {
            if (!TryRegisterTypeSignature(overload))
            {
                _stats.SkippedConflict++;
                continue;
            }
            output.Add(isInterface ? ToExpressionBodied(overload) : overload);
        }
    }

    // -----------------------------------------------------------------------
    // Constructors
    // -----------------------------------------------------------------------

    private void ProcessConstructor(
        ConstructorDeclarationSyntax ctor,
        List<MemberDeclarationSyntax> output,
        bool isStruct)
    {
        var visited = base.VisitConstructorDeclaration(ctor) as ConstructorDeclarationSyntax ?? ctor;

        if (ctor.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
            || !HasDefaults(ctor.ParameterList.Parameters))
        {
            output.Add(visited);
            return;
        }

        if (HasCallerAttribute(ctor.ParameterList))
        {
            _stats.SkippedUnsupported++;
            output.Add(visited);
            return;
        }

        Changed = true;
        _stats.DeclarationsStripped++;
        CountStrippedParameters(ctor.ParameterList.Parameters);

        var strippedCtor = visited.WithParameterList(StripDefaults(ctor.ParameterList));

        if (_options.GenerateOverloads && _model != null
            && !_typeHasJsonConstructor && !_typeAddedJsonConstructor
            && !HasJsonConstructorAttribute(strippedCtor))
        {
            // When the original constructor had a default value and overload
            // generation will create additional constructors, the type ends up
            // with multiple parameterized constructors. System.Text.Json selects
            // the constructor to use for deserialization by looking for a unique
            // parameterized constructor (or [JsonConstructor]). Without the
            // attribute, it throws NotSupportedException. Annotate the stripped
            // original — which accepts all parameters — so deserialization
            // keeps binding every JSON property to a constructor argument, the
            // same behaviour as before the default was removed.
            var annotatedCtor = AddJsonConstructorAttribute(strippedCtor);
            if (annotatedCtor != strippedCtor)
            {
                strippedCtor = annotatedCtor;
                _typeAddedJsonConstructor = true;
            }
        }

        output.Add(strippedCtor);

        if (!_options.GenerateOverloads || _model == null)
        {
            return;
        }

        foreach (var overload in DefaultParameterOverloadBuilder.BuildOverloads(
                     ctor, _model, _stats, isStruct, _catalog))
        {
            if (!TryRegisterTypeSignature(overload))
            {
                _stats.SkippedConflict++;
                continue;
            }
            output.Add(overload);
        }
    }

    // -----------------------------------------------------------------------
    // Local functions
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var saved = _localFunctionSignatures;
        _localFunctionSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in node.Statements)
        {
            if (statement is LocalFunctionStatementSyntax local)
            {
                RegisterLocalFunctionSignature(local);
            }
        }

        var newStatements = new List<StatementSyntax>(node.Statements.Count);
        foreach (var statement in node.Statements)
        {
            if (statement is LocalFunctionStatementSyntax local)
            {
                ProcessLocalFunction(local, newStatements);
                continue;
            }

            if (Visit(statement) is StatementSyntax visited)
            {
                newStatements.Add(visited);
            }
        }

        _localFunctionSignatures = saved;
        return node.WithStatements(SyntaxFactory.List(newStatements));
    }

    public override SyntaxNode? VisitGlobalStatement(GlobalStatementSyntax node)
    {
        if (node.Statement is LocalFunctionStatementSyntax local)
        {
            var statements = new List<StatementSyntax>();
            ProcessLocalFunction(local, statements);
            if (statements.Count == 1)
            {
                return node.WithStatement(statements[0]);
            }
            return SyntaxFactory.Block(statements);
        }

        return base.VisitGlobalStatement(node);
    }

    private void ProcessLocalFunction(
        LocalFunctionStatementSyntax function,
        List<StatementSyntax> output)
    {
        var visited = base.VisitLocalFunctionStatement(function) as LocalFunctionStatementSyntax ?? function;

        if (function.ParameterList == null || !HasDefaults(function.ParameterList.Parameters))
        {
            output.Add(visited);
            return;
        }

        if (HasCallerAttribute(function.ParameterList))
        {
            _stats.SkippedUnsupported++;
            output.Add(visited);
            return;
        }

        Changed = true;
        _stats.DeclarationsStripped++;
        CountStrippedParameters(function.ParameterList.Parameters);

        output.Add(visited.WithParameterList(StripDefaults(function.ParameterList)));

        if (!_options.GenerateOverloads || _model == null)
        {
            return;
        }

        foreach (var overload in DefaultParameterOverloadBuilder.BuildOverloads(function, _model, _stats))
        {
            var key = BuildLocalSignatureKey(overload);
            if (!_localFunctionSignatures.Add(key))
            {
                _stats.SkippedConflict++;
                continue;
            }
            output.Add(overload);
        }
    }

    // -----------------------------------------------------------------------
    // Delegates
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        if (node.ParameterList == null || !HasDefaults(node.ParameterList.Parameters))
        {
            return base.VisitDelegateDeclaration(node);
        }

        if (HasCallerAttribute(node.ParameterList))
        {
            _stats.SkippedUnsupported++;
            return base.VisitDelegateDeclaration(node);
        }

        Changed = true;
        _stats.DeclarationsStripped++;
        CountStrippedParameters(node.ParameterList.Parameters);

        var stripped = node.WithParameterList(StripDefaults(node.ParameterList));
        return base.VisitDelegateDeclaration(stripped);
    }

    // -----------------------------------------------------------------------
    // Call-site completion
    // -----------------------------------------------------------------------

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (_model != null && node.ArgumentList != null)
        {
            var completed = TryComplete(node, node.ArgumentList, TryGetInvocationName(node));
            if (completed != null)
            {
                node = node.WithArgumentList(completed);
                Changed = true;
            }
        }

        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (_model != null && node.ArgumentList != null)
        {
            var completed = TryComplete(node, node.ArgumentList, TryGetSimpleTypeName(node.Type));
            if (completed != null)
            {
                node = node.WithArgumentList(completed);
                Changed = true;
            }
        }

        return base.VisitObjectCreationExpression(node);
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        if (_model != null && node.ArgumentList != null)
        {
            var completed = TryComplete(node, node.ArgumentList, null);
            if (completed != null)
            {
                node = node.WithArgumentList(completed);
                Changed = true;
            }
        }

        return base.VisitImplicitObjectCreationExpression(node);
    }

    private static string? TryGetSimpleTypeName(TypeSyntax type)
        => type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => TryGetSimpleTypeName(qualified.Right),
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
            _ => null,
        };

    public override SyntaxNode? VisitConstructorInitializer(ConstructorInitializerSyntax node)
    {
        if (_model != null && node.ArgumentList != null)
        {
            var completed = TryComplete(node, node.ArgumentList, ".ctor");
            if (completed != null)
            {
                node = node.WithArgumentList(completed);
                Changed = true;
            }
        }

        return base.VisitConstructorInitializer(node);
    }

    public override SyntaxNode? VisitPrimaryConstructorBaseType(PrimaryConstructorBaseTypeSyntax node)
    {
        if (_model != null && node.ArgumentList != null)
        {
            var completed = TryComplete(node, node.ArgumentList, ".ctor");
            if (completed != null)
            {
                node = node.WithArgumentList(completed);
                Changed = true;
            }
        }

        return base.VisitPrimaryConstructorBaseType(node);
    }

    public override SyntaxNode? VisitAttribute(AttributeSyntax node)
    {
        if (_model != null)
        {
            var completed = TryCompleteAttribute(node);
            if (completed != null)
            {
                node = node.WithArgumentList(completed);
                Changed = true;
            }
        }

        return base.VisitAttribute(node);
    }

    private AttributeArgumentListSyntax? TryCompleteAttribute(AttributeSyntax node)
    {
        DefaultParameterSignature? signature;
        try
        {
            var symbolInfo = _model!.GetSymbolInfo(node);
            signature = symbolInfo.Symbol is IMethodSymbol methodSymbol
                ? _catalog.TryGetSignature(methodSymbol)
                : null;
        }
        catch (ArgumentException)
        {
            signature = null;
        }

        if (signature == null)
        {
            return null;
        }

        var args = node.ArgumentList?.Arguments ?? default;
        var count = signature.Count;
        var slots = new AttributeArgumentSyntax?[count];
        var slotsNamed = new bool[count];
        var nextPositional = 0;

        foreach (var arg in args)
        {
            if (arg.NameColon != null)
            {
                var index = signature.IndexOfName(arg.NameColon.Name.Identifier.Text);
                if (index < 0) return null;
                slots[index] = arg;
                slotsNamed[index] = true;
                if (index >= nextPositional) nextPositional = index + 1;
            }
            else if (arg.NameEquals != null)
            {
                // Named property/field assignment — does not occupy a parameter slot.
                continue;
            }
            else
            {
                if (nextPositional >= count) return null;
                slots[nextPositional] = arg;
                nextPositional++;
            }
        }

        var changed = false;
        for (var i = 0; i < count; i++)
        {
            if (slots[i] != null) continue;
            if (signature.IsParams[i]) continue;
            if (signature.DefaultTexts[i] == null) return null;

            slots[i] = SyntaxFactory.AttributeArgument(
                null,
                SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(signature.Names[i])),
                SyntaxFactory.ParseExpression(signature.DefaultTexts[i]!));
            slotsNamed[i] = true;
            changed = true;
        }

        if (!changed) return null;

        var result = new List<AttributeArgumentSyntax>();
        var seenNamed = false;
        for (var i = 0; i < count; i++)
        {
            if (slots[i] == null) continue;
            if (slotsNamed[i])
            {
                seenNamed = true;
                result.Add(slots[i]!);
            }
            else if (!seenNamed)
            {
                result.Add(slots[i]!);
            }
            else
            {
                result.Add(slots[i]!.WithNameColon(
                    SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(signature.Names[i]))));
            }
        }

        foreach (var arg in args)
        {
            if (arg.NameEquals != null)
            {
                result.Add(arg);
            }
        }

        return SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(result));
    }

    /// <summary>
    /// Completes an argument list so every parameter of the resolved callee is
    /// supplied. Returns <see langword="null"/> when nothing has to change.
    /// </summary>
    private ArgumentListSyntax? TryComplete(SyntaxNode node, ArgumentListSyntax argList, string? fallbackName)
    {
        var signature = ResolveSignature(node, fallbackName, argList.Arguments.Count);
        if (signature == null)
        {
            return null;
        }

        var args = argList.Arguments;
        var count = signature.Count;
        if (count == 0)
        {
            return null;
        }

        var slots = new ArgumentSyntax?[count];
        var slotsNamed = new bool[count];
        var nextPositional = 0;

        foreach (var arg in args)
        {
            if (arg.NameColon != null)
            {
                var index = signature.IndexOfName(arg.NameColon.Name.Identifier.Text);
                if (index < 0) return null;
                slots[index] = arg;
                slotsNamed[index] = true;
                if (index >= nextPositional) nextPositional = index + 1;
            }
            else
            {
                if (nextPositional >= count) return null;
                slots[nextPositional] = arg;
                nextPositional++;
            }
        }

        var changed = false;
        for (var i = 0; i < count; i++)
        {
            if (slots[i] != null) continue;
            if (signature.IsParams[i]) continue;
            if (signature.DefaultTexts[i] == null) return null;

            // The default expression is copied verbatim, so it must also be
            // resolvable at the call site — declarations frequently use private
            // constants (e.g. `int capacity = DefaultCapacity`) that are not in
            // scope for the caller. Leave those call sites alone; the generated
            // forwarding overload handles them inside the declaring type.
            if (!IsDefaultUsableAt(signature.DefaultTexts[i]!, node.SpanStart)) return null;

            slots[i] = SyntaxFactory.Argument(
                SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(signature.Names[i])),
                default,
                SyntaxFactory.ParseExpression(signature.DefaultTexts[i]!));
            slotsNamed[i] = true;
            changed = true;
        }

        if (!changed) return null;

        var result = new List<ArgumentSyntax>(count);
        var seenNamed = false;
        for (var i = 0; i < count; i++)
        {
            if (slots[i] == null) continue;
            if (slotsNamed[i])
            {
                seenNamed = true;
                result.Add(slots[i]!);
            }
            else if (!seenNamed)
            {
                result.Add(slots[i]!);
            }
            else
            {
                result.Add(slots[i]!.WithNameColon(
                    SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(signature.Names[i]))));
            }
        }

        _stats.CallSitesRewritten++;
        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(result));
    }

    private DefaultParameterSignature? ResolveSignature(SyntaxNode node, string? fallbackName, int argCount)
    {
        try
        {
            var symbolInfo = _model!.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
            {
                var signature = _catalog.TryGetSignature(methodSymbol);
                if (signature != null)
                {
                    return signature;
                }
            }
        }
        catch (ArgumentException)
        {
            // The node is not part of the semantic model's tree (it was created
            // by an earlier rewrite step) — fall through to the name-based lookup.
        }

        // Syntax-based fallback. Matching purely by name and arity is only safe
        // when the call site itself names at least one parameter: every provided
        // name is validated against the candidate below, and a purely positional
        // call cannot be attributed to a declaration reliably (it may target a
        // completely unrelated type that happens to use the same member name).
        if (fallbackName == null || !HasNamedArguments(node))
        {
            return null;
        }

        for (var candidateCount = argCount; candidateCount <= argCount + 8; candidateCount++)
        {
            var byName = _catalog.TryGetSignatureByName(fallbackName, candidateCount);
            if (byName != null && byName.HasAnyDefault)
            {
                return byName;
            }
        }

        return null;
    }

    private static bool HasNamedArguments(SyntaxNode node)
    {
        var arguments = node switch
        {
            InvocationExpressionSyntax invocation => invocation.ArgumentList?.Arguments,
            ObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments,
            ImplicitObjectCreationExpressionSyntax implicitCreation => implicitCreation.ArgumentList?.Arguments,
            ConstructorInitializerSyntax initializer => initializer.ArgumentList?.Arguments,
            PrimaryConstructorBaseTypeSyntax primaryBase => primaryBase.ArgumentList?.Arguments,
            _ => null,
        };

        if (arguments == null)
        {
            return false;
        }

        foreach (var argument in arguments)
        {
            if (argument.NameColon != null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="defaultText"/> can be injected at
    /// <paramref name="position"/>. Literals, <c>default</c> and <c>new</c>
    /// expressions always qualify; anything naming a symbol is looked up in the
    /// scope of the call site.
    /// </summary>
    private bool IsDefaultUsableAt(string defaultText, int position)
    {
        ExpressionSyntax expression;
        try
        {
            expression = SyntaxFactory.ParseExpression(defaultText);
        }
        catch (Exception)
        {
            return false;
        }

        var rootName = GetRootName(expression);
        if (rootName == null)
        {
            return true;
        }

        try
        {
            return _model!.LookupSymbols(position, name: rootName).Length > 0;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// Left-most name of an expression: <c>E.A</c> → <c>E</c>,
    /// <c>TestPlatforms.Any</c> → <c>TestPlatforms</c>. Returns
    /// <see langword="null"/> for expressions that reference no symbol at all
    /// (literals, <c>default</c>, <c>new</c>, casts of predefined types).
    /// </summary>
    private static string? GetRootName(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.ValueText;
                case GenericNameSyntax generic:
                    return generic.Identifier.ValueText;
                case MemberAccessExpressionSyntax { Expression: var inner }:
                    expression = inner;
                    continue;
                case ConditionalAccessExpressionSyntax { Expression: var inner }:
                    expression = inner;
                    continue;
                case InvocationExpressionSyntax { Expression: var inner }:
                    expression = inner;
                    continue;
                case ElementAccessExpressionSyntax { Expression: var inner }:
                    expression = inner;
                    continue;
                default:
                    return null;
            }
        }
    }

    private static string? TryGetInvocationName(InvocationExpressionSyntax node)
        => node.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            MemberAccessExpressionSyntax ma when ma.Name is IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma when ma.Name is GenericNameSyntax generic => generic.Identifier.Text,
            MemberBindingExpressionSyntax mb when mb.Name is IdentifierNameSyntax id => id.Identifier.Text,
            _ => null,
        };

    // -----------------------------------------------------------------------
    // Duplicate-signature bookkeeping
    // -----------------------------------------------------------------------

    private void RegisterTypeMemberSignature(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                _typeSignatures.Add(BuildMethodSignatureKey(method.Identifier.Text, method.ParameterList));
                break;
            case ConstructorDeclarationSyntax ctor:
                _typeSignatures.Add(BuildMethodSignatureKey(".ctor", ctor.ParameterList));
                break;
        }
    }

    private bool TryRegisterTypeSignature(BaseMethodDeclarationSyntax overload)
    {
        var name = overload is ConstructorDeclarationSyntax ctor
            ? ".ctor"
            : ((MethodDeclarationSyntax)overload).Identifier.Text;
        return _typeSignatures.Add(BuildMethodSignatureKey(name, overload.ParameterList));
    }

    private static string BuildMethodSignatureKey(string name, ParameterListSyntax parameterList)
    {
        var types = new List<string>(parameterList.Parameters.Count);
        foreach (var parameter in parameterList.Parameters)
        {
            types.Add(NormalizeType(parameter.Type?.ToString() ?? ""));
        }
        return $"{name}({string.Join(",", types)})";
    }

    /// <summary>
    /// Normalises a parameter type so that semantically identical spellings
    /// (<c>int?</c> / <c>System.Nullable&lt;int&gt;</c>, <c>string</c> /
    /// <c>String</c>) map onto the same key.
    /// </summary>
    private static string NormalizeType(string type)
    {
        var normalized = type.Replace(" ", "");
        switch (normalized)
        {
            case "String": return "string";
            case "Int32": return "int";
            case "Boolean": return "bool";
            case "Object": return "object";
            case "Double": return "double";
            case "Single": return "float";
            case "Char": return "char";
            case "Byte": return "byte";
            case "Decimal": return "decimal";
        }
        return normalized;
    }

    private void RegisterLocalFunctionSignature(LocalFunctionStatementSyntax function)
        => _localFunctionSignatures.Add(BuildLocalSignatureKey(function));

    private static string BuildLocalSignatureKey(LocalFunctionStatementSyntax function)
    {
        var types = new List<string>();
        if (function.ParameterList != null)
        {
            foreach (var parameter in function.ParameterList.Parameters)
            {
                types.Add(NormalizeType(parameter.Type?.ToString() ?? ""));
            }
        }
        return $"{function.Identifier.Text}({string.Join(",", types)})";
    }

    // -----------------------------------------------------------------------
    // JsonConstructor helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// True when any constructor of the type is already annotated with
    /// <c>[JsonConstructor]</c>. At most one constructor per type may carry
    /// the attribute, so strip-default must never add a second one.
    /// </summary>
    private static bool TypeHasJsonConstructor(SyntaxList<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            if (member is ConstructorDeclarationSyntax ctor
                && HasJsonConstructorAttribute(ctor))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasJsonConstructorAttribute(ConstructorDeclarationSyntax ctor)
    {
        foreach (var list in ctor.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                var name = attribute.Name.ToString();
                if (name.EndsWith("JsonConstructor", StringComparison.Ordinal)
                    || name.EndsWith("JsonConstructorAttribute", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Adds <c>[JsonConstructor]</c> to a constructor so that
    /// System.Text.Json keeps using it for deserialization after
    /// strip-default has generated forwarding overloads.
    ///
    /// Returns the constructor unchanged when the attribute name
    /// cannot be resolved (see <see cref="ResolveJsonConstructorAttributeName"/>).
    /// </summary>
    private ConstructorDeclarationSyntax AddJsonConstructorAttribute(
        ConstructorDeclarationSyntax ctor)
    {
        var name = ResolveJsonConstructorAttributeName();
        if (name == null)
        {
            // Cannot resolve JsonConstructor in this file — skip.
            return ctor;
        }

        var attribute = SyntaxFactory.Attribute(SyntaxFactory.ParseName(name));
        var attributeList = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(attribute));
        return ctor.WithAttributeLists(ctor.AttributeLists.Add(attributeList));
    }

    /// <summary>
    /// Returns the attribute name to emit for <c>JsonConstructor</c>, or
    /// <c>null</c> when no attribute should be added.
    ///
    /// The attribute is only emitted when the current file already
    /// references a <c>System.Text.Json.Serialization</c> attribute (such
    /// as <c>[JsonConstructor]</c>, <c>[JsonIgnore]</c>,
    /// <c>[JsonPropertyName]</c>, etc.) — this is a reliable signal that
    /// the file participates in System.Text.Json (de)serialisation and
    /// therefore the project references the correct assembly.
    ///
    /// When emitted, the bare name <c>JsonConstructor</c> is used so it
    /// resolves to the same attribute type the project's own serialiser
    /// checks for (critical for renamed copies of System.Text.Json).
    /// </summary>
    private string? ResolveJsonConstructorAttributeName()
    {
        if (_model == null)
            return null;

        try
        {
            // Check whether the current file already uses any
            // System.Text.Json.Serialization attribute. This is the
            // signal that the file's project references the correct
            // assembly and the bare name will resolve.
            if (!FileUsesJsonAttributes(_model.SyntaxTree))
                return null;

            // Determine the namespace of JsonConstructorAttribute.
            // Prefer a source-defined type (renamed copy), then fall back
            // to the inbox System.Text.Json.
            foreach (var ns in s_candidateNamespaces)
            {
                var typeSymbol = _model.Compilation.GetTypeByMetadataName(
                    $"{ns}.JsonConstructorAttribute");
                if (typeSymbol != null && IsDefinedInCompilationAssembly(typeSymbol))
                {
                    // Source-defined — use bare name so it binds to the
                    // project's own type. The file already has the right
                    // usings (verified above).
                    return "JsonConstructor";
                }
            }

            // No source-defined attribute; check if the file has a using
            // directive for the namespace. If so, use the bare name — it
            // resolves via the using and is shorter. Otherwise use the
            // fully-qualified name.
            foreach (var ns in s_candidateNamespaces)
            {
                var typeSymbol = _model.Compilation.GetTypeByMetadataName(
                    $"{ns}.JsonConstructorAttribute");
                if (typeSymbol != null)
                {
                    if (FileHasUsingDirective(ns))
                        return "JsonConstructor";
                    return $"{ns}.JsonConstructor";
                }
            }

            // The file uses JSON attributes (verified above) so it already
            // has the correct using directives. Use the bare name — it
            // will resolve via the existing using. This handles the case
            // where the compilation references System.Text.Json but the
            // semantic model cannot resolve the type by metadata name
            // (e.g. when the reference is a facade or the assembly is
            // loaded via AssemblyLoadContext).
            return "JsonConstructor";
        }
        catch (Exception)
        {
            // Fall through
        }
        return null;
    }

    /// <summary>
    /// True when the syntax tree has a <c>using &lt;namespace&gt;;</c>
    /// directive (non-global) for the specified namespace.
    /// </summary>
    private bool FileHasUsingDirective(string namespaceName)
    {
        if (_model == null)
            return false;

        var root = _model.SyntaxTree.GetRoot() as CompilationUnitSyntax;
        if (root == null)
            return false;

        foreach (var use in root.Usings)
        {
            // Skip global usings (C# 10): they have a "global" keyword prefix.
            var nameStr = use.Name?.ToString();
            if (nameStr != null
                && string.Equals(nameStr, namespaceName, StringComparison.Ordinal))
            {
                // Check that this is not a global using by looking at the
                // raw text — global usings start with "global ".
                var fullText = use.ToString();
                if (!fullText.StartsWith("global ", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the syntax tree contains any attribute whose name ends
    /// with a known System.Text.Json.Serialization attribute name
    /// (JsonConstructor, JsonIgnore, JsonPropertyName, JsonConverter,
    /// JsonExtensionData, JsonInclude, JsonNumberHandling).
    /// </summary>
    private static bool FileUsesJsonAttributes(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            if (node is not AttributeSyntax attribute)
                continue;

            var name = attribute.Name.ToString();
            foreach (var suffix in s_jsonAttributeSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static readonly string[] s_jsonAttributeSuffixes =
    [
        "JsonConstructor",
        "JsonConstructorAttribute",
        "JsonIgnore",
        "JsonIgnoreAttribute",
        "JsonPropertyName",
        "JsonPropertyNameAttribute",
        "JsonConverter",
        "JsonConverterAttribute",
        "JsonExtensionData",
        "JsonExtensionDataAttribute",
        "JsonInclude",
        "JsonIncludeAttribute",
        "JsonNumberHandling",
        "JsonNumberHandlingAttribute",
    ];

    /// <summary>
    /// True when the symbol's containing assembly is also one of the
    /// compilation's own syntax trees (i.e. the type is source-defined,
    /// not merely referenced).
    /// </summary>
    private bool IsDefinedInCompilationAssembly(ITypeSymbol typeSymbol)
    {
        var assembly = typeSymbol.ContainingAssembly;
        if (assembly == null)
            return false;

        return string.Equals(
            assembly.Name,
            _model?.Compilation.AssemblyName,
            StringComparison.Ordinal);
    }

    private static readonly string[] s_candidateNamespaces =
    [
        "dotnet.text.json.Serialization",
        "System.Text.Json.Serialization",
    ];

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool HasPrimaryDefaults(TypeDeclarationSyntax node)
        => node.ParameterList != null && HasDefaults(node.ParameterList.Parameters);

    private static bool HasDirtyMembers(SyntaxList<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method when HasDefaults(method.ParameterList.Parameters):
                    return true;
                case ConstructorDeclarationSyntax ctor when HasDefaults(ctor.ParameterList.Parameters):
                    return true;
                case TypeDeclarationSyntax type when HasDirtyMembers(type.Members) || HasPrimaryDefaults(type):
                    return true;
            }
        }
        return false;
    }

    private ParameterListSyntax StripDefaults(ParameterListSyntax parameterList)
    {
        var newParameters = new List<ParameterSyntax>(parameterList.Parameters.Count);
        foreach (var parameter in parameterList.Parameters)
        {
            if (parameter.Default == null)
            {
                newParameters.Add(parameter);
                continue;
            }

            var stripped = parameter.WithDefault(null);
            var metadataAttribute = TryBuildDefaultValueMetadataAttribute(parameter);
            if (metadataAttribute != null)
            {
                stripped = stripped.WithAttributeLists(stripped.AttributeLists.Add(metadataAttribute));
            }
            newParameters.Add(stripped);
        }
        return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParameters));
    }

    /// <summary>
    /// Removing <c>= value</c> also removes the default from the emitted
    /// metadata, which reflection-driven code observes through
    /// <see cref="System.Reflection.ParameterInfo.HasDefaultValue"/> —
    /// serializers and DI containers change behaviour when it disappears.
    ///
    /// <c>[DefaultParameterValue]</c> restores the metadata constant without
    /// making the parameter optional: <c>[Optional]</c> is deliberately omitted,
    /// so every call site still has to pass the argument explicitly.
    /// </summary>
    private AttributeListSyntax? TryBuildDefaultValueMetadataAttribute(ParameterSyntax parameter)
    {
        if (!_options.PreserveDefaultMetadata || parameter.Default == null)
        {
            return null;
        }

        foreach (var existing in parameter.AttributeLists)
        {
            foreach (var existingAttribute in existing.Attributes)
            {
                if (existingAttribute.Name.ToString().EndsWith("DefaultParameterValue", StringComparison.Ordinal)
                    || existingAttribute.Name.ToString().EndsWith("DefaultParameterValueAttribute", StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }

        var expression = parameter.Default.Value;
        var literal = TryRenderDefaultMetadataValue(expression, parameter);
        if (literal == null)
        {
            return null;
        }

        // The attribute argument is boxed, and the compiler requires its
        // compile-time type to match the parameter exactly: an unsuffixed
        // numeric literal is an int and is rejected for sbyte/byte/short/
        // ushort/uint/ulong parameters (CS1908). Wrap the constant in a cast
        // of the parameter's (underlying, for nullable) integral type.
        var castType = TryGetIntegralCastTypeText(parameter);
        if (castType != null && IsNumericConstant(expression))
        {
            literal = $"({castType}){literal}";
        }

        var attribute = SyntaxFactory.Attribute(
            SyntaxFactory.ParseName("global::System.Runtime.InteropServices.DefaultParameterValue"),
            SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression(literal)))));

        return SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute));
    }

    /// <summary>
    /// Renders a default expression as text that is legal inside an attribute
    /// argument. Returns <see langword="null"/> when the value cannot be
    /// expressed as a metadata constant.
    /// </summary>
    private string? TryRenderDefaultMetadataValue(ExpressionSyntax expression, ParameterSyntax parameter)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return TryRenderDefaultMetadataValue(parenthesized.Expression, parameter);

            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.DefaultLiteralExpression):
            case DefaultExpressionSyntax:
                return RenderTypeDefault(expression, parameter);

            case LiteralExpressionSyntax literal:
                return RenderLiteral(literal, parameter);

            case PrefixUnaryExpressionSyntax unary
                when unary.IsKind(SyntaxKind.UnaryMinusExpression)
                    || unary.IsKind(SyntaxKind.UnaryPlusExpression):
                var inner = TryRenderDefaultMetadataValue(unary.Operand, parameter);
                return inner == null ? null : unary.OperatorToken.Text + inner;

            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                // enum members, const fields, `nameof(...)`-style constants
                return IsCompileTimeConstant(expression) ? expression.ToString() : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Renders a literal default value.
    ///
    /// <c>null</c> is a legal attribute argument only for reference-typed
    /// parameters: on a nullable value type the compiler rejects
    /// <c>[DefaultParameterValue(null)]</c> (CS1908), and a <c>(T?)null</c>
    /// cast is not a constant expression (CS0182) — so for those the metadata
    /// attribute is dropped entirely.
    ///
    /// <c>decimal</c> literals are never valid attribute arguments (CS0182).
    /// </summary>
    private string? RenderLiteral(LiteralExpressionSyntax literal, ParameterSyntax parameter)
    {
        if (literal.IsKind(SyntaxKind.NullLiteralExpression))
        {
            var type = GetParameterTypeSymbol(parameter);
            return type != null && !type.IsValueType ? "null" : null;
        }

        if (literal.IsKind(SyntaxKind.NumericLiteralExpression)
            && GetParameterTypeSymbol(parameter)?.SpecialType == SpecialType.System_Decimal)
        {
            return null;
        }

        return literal.Token.Text;
    }

    /// <summary>
    /// Renders the default of a <c>default</c> / <c>default(T)</c> expression.
    /// Integral literals are spelled with the exact parameter type because an
    /// unsuffixed <c>0</c> is an int (CS1908 for smaller/unsigned types).
    /// </summary>
    private string? RenderTypeDefault(ExpressionSyntax expression, ParameterSyntax parameter)
    {
        var type = _model?.GetTypeInfo(expression).Type ?? GetParameterTypeSymbol(parameter);
        if (type == null)
        {
            return null;
        }

        if (!type.IsValueType)
        {
            return "null";
        }

        // A nullable value type's default is null, which has no legal source
        // spelling inside the attribute (see RenderLiteral). Non-null constant
        // defaults of nullables never take this path — they are rendered by the
        // literal / constant cases and match the underlying type.
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return null;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return $"({parameter.Type!.ToString()})0";
        }

        return type.SpecialType switch
        {
            SpecialType.System_Boolean => "false",
            SpecialType.System_Char => "'\\0'",
            SpecialType.System_SByte => "(sbyte)0",
            SpecialType.System_Byte => "(byte)0",
            SpecialType.System_Int16 => "(short)0",
            SpecialType.System_UInt16 => "(ushort)0",
            SpecialType.System_Int32 => "0",
            SpecialType.System_UInt32 => "(uint)0",
            SpecialType.System_Int64 => "0L",
            SpecialType.System_UInt64 => "(ulong)0",
            SpecialType.System_Single => "0F",
            SpecialType.System_Double => "0D",
            _ => null, // structs such as Guid, and decimal: no metadata constant exists either way
        };
    }

    private ITypeSymbol? GetParameterTypeSymbol(ParameterSyntax parameter)
    {
        if (_model == null || parameter.Type == null)
        {
            return null;
        }

        try
        {
            return _model.GetSymbolInfo(parameter.Type).Symbol as ITypeSymbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The predefined type keyword used to cast numeric constants so the boxed
    /// attribute argument matches the parameter type exactly, or
    /// <see langword="null"/> when no cast is required (int/long/float/double
    /// accept int literals through standard implicit conversions, and keywords
    /// need no name resolution).
    /// </summary>
    private string? TryGetIntegralCastTypeText(ParameterSyntax parameter)
    {
        var type = GetParameterTypeSymbol(parameter);
        if (type == null)
        {
            return null;
        }

        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && type is INamedTypeSymbol named
            && named.TypeArguments.Length == 1)
        {
            type = named.TypeArguments[0];
        }

        return type.SpecialType switch
        {
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Byte => "byte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_UInt64 => "ulong",
            _ => null,
        };
    }

    /// <summary>
    /// True when the expression is a (possibly signed or parenthesised)
    /// numeric literal — the only defaults the integral cast wrapping applies to.
    /// </summary>
    private static bool IsNumericConstant(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression):
                    return true;
                case PrefixUnaryExpressionSyntax unary
                    when unary.IsKind(SyntaxKind.UnaryMinusExpression)
                        || unary.IsKind(SyntaxKind.UnaryPlusExpression):
                    expression = unary.Operand;
                    continue;
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                default:
                    return false;
            }
        }
    }

    private bool IsCompileTimeConstant(ExpressionSyntax expression)
    {
        if (_model == null)
        {
            return false;
        }

        try
        {
            var constant = _model.GetConstantValue(expression);
            return constant.HasValue && constant.Value is not decimal;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a parameter carries a Caller attribute (CallerMemberName,
    /// CallerLineNumber, CallerFilePath). The C# compiler requires such
    /// parameters to declare a default value (CS4020/CS4022), so declarations
    /// owning them are left untouched.
    /// </summary>
    private static bool HasCallerAttribute(ParameterSyntax parameter)
    {
        foreach (var attributeList in parameter.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var name = attribute.Name.ToString();
                if (name.Contains("CallerMemberName")
                    || name.Contains("CallerLineNumber")
                    || name.Contains("CallerFilePath"))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasCallerAttribute(ParameterListSyntax parameterList)
        => parameterList.Parameters.Any(HasCallerAttribute);

    private static bool IsTestMethod(MethodDeclarationSyntax method)
    {
        foreach (var attributeList in method.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var name = attribute.Name.ToString();
                if (name.Contains("Fact") || name.Contains("Theory")
                    || name.Contains("Test") || name.Contains("TestMethod"))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasDefaults(SeparatedSyntaxList<ParameterSyntax> parameters)
        => parameters.Any(p => p.Default != null);

    private void CountStrippedParameters(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        foreach (var p in parameters)
        {
            if (p.Default != null)
            {
                _stats.ParametersStripped++;
            }
        }
    }

    private static MethodDeclarationSyntax ToExpressionBodied(MethodDeclarationSyntax method)
    {
        if (method.Body is { Statements.Count: 1 } body)
        {
            if (body.Statements[0] is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation })
            {
                return method.WithBody(null)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(invocation))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            }

            if (body.Statements[0] is ReturnStatementSyntax { Expression: { } returned })
            {
                return method.WithBody(null)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(returned))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            }
        }
        return method;
    }
}
