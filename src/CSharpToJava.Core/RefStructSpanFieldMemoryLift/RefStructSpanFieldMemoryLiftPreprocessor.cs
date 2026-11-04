using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefStructSpanFieldMemoryLift;

/// <summary>
/// Independent read-only post-pass <c>ref-struct-span-field-memory-lift</c>
/// (pass stage Declarative, runs right after <c>ref-struct-lowering</c>).
/// <para>
/// A user <c>ref struct</c> whose only ref-only storage is one or more
/// <c>ReadOnlySpan&lt;T&gt;</c> fields (or auto-properties) is lowered to an
/// ordinary struct: every span storage becomes a <c>ReadOnlyMemory&lt;T&gt;</c>
/// field plus a same-named span property, every simple assignment to the field
/// is rewritten through <c>__cs2j_ToMemory(...)</c>, and the <c>ref</c>
/// modifier is removed. This is what lets <c>Utf8JsonReader</c> stop being a
/// ref struct. The shared helper keeps <c>default</c> spans as <c>default</c>
/// memory so the lifted span property still compares equal to <c>default</c>.
/// </para>
/// <para>
/// The transformation is conservative: a declaration is preserved (RSM001)
/// when any field is a writable <c>Span&lt;T&gt;</c>/ref/pointer/function-pointer/
/// ref-like type, carries an initializer, is not private, is referenced through
/// <c>ref</c>/<c>out</c>/<c>&amp;</c>/<c>nameof</c>/a matching string literal,
/// or is assigned with a non-simple operator; nested ref structs also keep the
/// outer type's <c>ref</c> modifier. Partial declarations are handled at the
/// symbol level, so every partial declaration loses <c>ref</c> while the shared
/// helper is injected only once (into the declaration owning the first lifted
/// storage).
/// </para>
/// Only the intended members change; unchanged files are returned byte-for-byte
/// and re-running the pass over its own output is a no-op (idempotent).
/// </summary>
public sealed class RefStructSpanFieldMemoryLiftPreprocessor
{
    private const string HelperName = "__cs2j_ToMemory";
    private const string StoragePrefix = "__cs2j_";
    private const string StorageSuffix = "_memory";

    public RefStructSpanFieldMemoryLiftResult RewriteFile(
        string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new RefStructSpanFieldMemoryLiftStatistics();
        var diagnostics = new List<RefStructSpanFieldMemoryLiftDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;
        var compilation = semanticModel.Compilation;

        var plans = new Dictionary<ISymbol, TypePlan>(SymbolEqualityComparer.Default);
        var replacements =
            new List<(StructDeclarationSyntax Original, StructDeclarationSyntax Replacement, int Lifted)>();

        foreach (var declaration in root.DescendantNodes().OfType<StructDeclarationSyntax>())
        {
            if (!declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
            {
                continue;
            }

            if (semanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol)
            {
                statistics.RefStructsPreserved++;
                diagnostics.Add(CreatePreservedDiagnostic(
                    declaration, "the struct symbol could not be resolved", filePath));
                continue;
            }

            if (!plans.TryGetValue(symbol, out var plan))
            {
                plan = AnalyzeType(symbol, compilation);
                plans[symbol] = plan;
            }

            if (plan.RejectReason != null)
            {
                statistics.RefStructsPreserved++;
                diagnostics.Add(CreatePreservedDiagnostic(
                    declaration, plan.RejectReason, filePath));
                continue;
            }

            var liftedCount = BuildDeclaration(
                declaration, plan, semanticModel, out var replacement);
            replacements.Add((declaration, replacement, liftedCount));
        }

        if (replacements.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var map = new Dictionary<SyntaxNode, SyntaxNode>(
            replacements.Count, ReferenceEqualityComparer.Instance);
        foreach (var pair in replacements)
        {
            map[pair.Original] = pair.Replacement;
        }

        var newRoot = (CompilationUnitSyntax)root.ReplaceNodes(
            map.Keys, (original, _) => map[original]);
        var outputCode = newRoot.ToFullString();

        if (!ParsesWithoutErrors(outputCode))
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new RefStructSpanFieldMemoryLiftDiagnostic(
                RefStructSpanFieldMemoryLiftSeverity.Warning,
                RefStructSpanFieldMemoryLiftDiagnosticCode.RSM010,
                "ref-struct-span-field-memory-lift rewrite failed local parse validation; " +
                "the original file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        statistics.RefStructsLowered += replacements.Count;
        statistics.SpanFieldsMemoryLifted +=
            replacements.Sum(pair => pair.Lifted);
        return new RefStructSpanFieldMemoryLiftResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    // ── type-level eligibility ───────────────────────────────────────────

    private static TypePlan AnalyzeType(INamedTypeSymbol symbol, Compilation compilation)
    {
        var storages = new List<StorageLift>();

        foreach (var nested in symbol.GetTypeMembers())
        {
            if (nested.IsRefLikeType)
            {
                return TypePlan.Reject($"nested ref struct type '{nested.Name}'");
            }
        }

        foreach (var member in symbol.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field when !field.IsImplicitlyDeclared:
                    {
                        if (field.RefKind != RefKind.None)
                        {
                            return TypePlan.Reject($"ref field '{field.Name}'");
                        }

                        if (IsUnsupportedRefOnlyType(field.Type, out var fieldType))
                        {
                            return TypePlan.Reject(
                                $"field '{field.Name}' has ref-only type '{fieldType}'");
                        }

                        if (!IsReadOnlySpan(field.Type, out _))
                        {
                            // Ordinary class-safe field, including ReadOnlySequence etc.
                            continue;
                        }

                        var reason = ValidateFieldForLift(field, symbol, compilation, out var storage);
                        if (reason != null)
                        {
                            return TypePlan.Reject(reason);
                        }

                        storages.Add(storage!);
                        break;
                    }

                case IPropertySymbol property when !property.IsImplicitlyDeclared:
                    {
                        if (property.RefKind != RefKind.None)
                        {
                            return TypePlan.Reject(
                                $"ref-returning property '{property.Name}'");
                        }

                        if (!IsReadOnlySpan(property.Type, out _))
                        {
                            if (IsUnsupportedRefOnlyType(property.Type, out var propertyType)
                                && IsAutoProperty(property))
                            {
                                return TypePlan.Reject(
                                    $"auto-property '{property.Name}' has ref-only type " +
                                    $"'{propertyType}'");
                            }

                            continue;
                        }

                        if (!IsAutoProperty(property))
                        {
                            // A computed span property owns no backing field.
                            continue;
                        }

                        var reason = ValidateAutoPropertyForLift(
                            property, symbol, out var storage);
                        if (reason != null)
                        {
                            return TypePlan.Reject(reason);
                        }

                        storages.Add(storage!);
                        break;
                    }
            }
        }

        if (storages.Count == 0)
        {
            return TypePlan.Reject("no ReadOnlySpan<T> field");
        }

        foreach (var storage in storages)
        {
            if (MemberNameExists(symbol, storage.MemoryName))
            {
                return TypePlan.Reject(
                    $"generated member name '{storage.MemoryName}' already exists");
            }
        }

        if (MemberNameExists(symbol, HelperName))
        {
            return TypePlan.Reject($"generated helper name '{HelperName}' already exists");
        }

        var host = FindHelperHost(storages);
        return new TypePlan(storages, null, host);
    }

    private static string? ValidateFieldForLift(
        IFieldSymbol field,
        INamedTypeSymbol owner,
        Compilation compilation,
        out StorageLift? storage)
    {
        storage = null;
        var spanType = (INamedTypeSymbol)field.Type;

        var references = field.DeclaringSyntaxReferences;
        if (references.Length != 1)
        {
            return $"field '{field.Name}' has no single declaring syntax";
        }

        if (references[0].GetSyntax() is not VariableDeclaratorSyntax declarator
            || declarator.Parent is not VariableDeclarationSyntax declaration
            || declaration.Variables.Count != 1)
        {
            return $"field '{field.Name}' is not declared by a single declarator";
        }

        if (declarator.Initializer != null)
        {
            return $"field '{field.Name}' has an initializer";
        }

        if (field.IsStatic || field.IsConst)
        {
            return $"field '{field.Name}' is static or const";
        }

        if (field.DeclaredAccessibility != Accessibility.Private)
        {
            return $"field '{field.Name}' is not private";
        }

        var scan = ScanFieldReferences(owner, field, compilation);
        if (scan.UnsafeReason != null)
        {
            return $"field '{field.Name}' {scan.UnsafeReason}";
        }

        if (scan.NonSimpleAssignment)
        {
            return $"field '{field.Name}' is assigned with a non-simple operator";
        }

        var memoryName = BuildMemoryName(field.Name);
        storage = new StorageLift(
            field,
            field.Name,
            memoryName,
            spanType,
            false,
            scan.SimpleAssignments);
        return null;
    }

    private static string? ValidateAutoPropertyForLift(
        IPropertySymbol property,
        INamedTypeSymbol owner,
        out StorageLift? storage)
    {
        storage = null;
        var spanType = (INamedTypeSymbol)property.Type;

        var references = property.DeclaringSyntaxReferences;
        if (references.Length != 1
            || references[0].GetSyntax() is not PropertyDeclarationSyntax syntax)
        {
            return $"auto-property '{property.Name}' has no single declaring syntax";
        }

        if (syntax.Initializer != null)
        {
            return $"auto-property '{property.Name}' has an initializer";
        }

        if (property.IsStatic)
        {
            return $"auto-property '{property.Name}' is static";
        }

        var memoryName = BuildMemoryName(property.Name);
        storage = new StorageLift(
            property,
            property.Name,
            memoryName,
            spanType,
            true,
            Array.Empty<AssignmentExpressionSyntax>());
        return null;
    }

    private static bool IsAutoProperty(IPropertySymbol property)
    {
        var references = property.DeclaringSyntaxReferences;
        if (references.Length != 1
            || references[0].GetSyntax() is not PropertyDeclarationSyntax syntax)
        {
            return false;
        }

        if (syntax.ExpressionBody != null || syntax.Initializer != null)
        {
            return false;
        }

        var accessors = syntax.AccessorList?.Accessors;
        if (accessors == null || accessors.Value.Count == 0)
        {
            return false;
        }

        if (!accessors.Value.Any(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration)))
        {
            return false;
        }

        return accessors.Value.All(accessor =>
            accessor.Body == null && accessor.ExpressionBody == null);
    }

    private static bool IsUnsupportedRefOnlyType(ITypeSymbol type, out string displayName)
    {
        displayName = type.ToDisplayString();
        switch (type)
        {
            case IPointerTypeSymbol:
                return true;

            case IFunctionPointerTypeSymbol:
                return true;

            case ITypeParameterSymbol { AllowsRefLikeType: true }:
                return true;

            case INamedTypeSymbol named:
                if (IsReadOnlySpan(named, out _))
                {
                    return false;
                }

                return named.IsRefLikeType;
        }

        return false;
    }

    private static bool IsReadOnlySpan(ITypeSymbol type, out ITypeSymbol elementType)
    {
        elementType = type;
        if (type is not INamedTypeSymbol named
            || named.Arity != 1
            || named.Name != "ReadOnlySpan"
            || named.ContainingNamespace.ToDisplayString() != "System")
        {
            return false;
        }

        elementType = named.TypeArguments[0];
        return true;
    }

    private static bool MemberNameExists(INamedTypeSymbol symbol, string name)
        => symbol.GetMembers().Any(member =>
            string.Equals(member.Name, name, StringComparison.Ordinal));

    private static string BuildMemoryName(string memberName)
    {
        var trimmed = memberName.TrimStart('_');
        if (trimmed.Length == 0)
        {
            trimmed = "value";
        }

        return StoragePrefix + trimmed + StorageSuffix;
    }

    private static SyntaxReference? FindHelperHost(IReadOnlyList<StorageLift> storages)
    {
        SyntaxReference? host = null;
        (string Path, int Start)? hostKey = null;

        foreach (var storage in storages)
        {
            foreach (var reference in storage.Symbol.DeclaringSyntaxReferences)
            {
                var key = (reference.SyntaxTree.FilePath, reference.Span.Start);
                if (hostKey == null
                    || string.CompareOrdinal(key.FilePath, hostKey.Value.Path) < 0
                    || (string.CompareOrdinal(key.FilePath, hostKey.Value.Path) == 0
                        && key.Start < hostKey.Value.Start))
                {
                    hostKey = key;
                    host = reference;
                }
            }
        }

        return host;
    }

    // ── field reference scan ─────────────────────────────────────────────

    private static FieldReferenceScan ScanFieldReferences(
        INamedTypeSymbol owner, IFieldSymbol field, Compilation compilation)
    {
        var scan = new FieldReferenceScan();
        foreach (var declarationReference in owner.DeclaringSyntaxReferences)
        {
            var tree = declarationReference.SyntaxTree;
            var model = compilation.GetSemanticModel(tree);
            var declaration = declarationReference.GetSyntax();

            foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!string.Equals(
                        identifier.Identifier.ValueText, field.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!SymbolEqualityComparer.Default.Equals(
                        model.GetSymbolInfo(identifier).Symbol, field))
                {
                    continue;
                }

                if (FindUnsafeReferenceKind(identifier) is { } unsafeKind)
                {
                    scan.UnsafeReason = $"is referenced through {unsafeKind}";
                    return scan;
                }

                if (FindAssignmentTarget(identifier) is { } assignment)
                {
                    if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    {
                        scan.NonSimpleAssignment = true;
                        return scan;
                    }

                    scan.SimpleAssignments.Add(assignment);
                }
            }

            foreach (var literal in declaration.DescendantNodes()
                         .OfType<LiteralExpressionSyntax>())
            {
                if (literal.IsKind(SyntaxKind.StringLiteralExpression)
                    && string.Equals(
                        literal.Token.ValueText, field.Name, StringComparison.Ordinal))
                {
                    scan.UnsafeReason = "is referenced by a string literal (reflection name)";
                    return scan;
                }
            }
        }

        return scan;
    }

    /// <summary>
    /// Returns a short description when the identifier is used in a way a
    /// read-only property cannot serve (<c>ref</c>/<c>out</c>/<c>in</c> argument,
    /// <c>&amp;</c>, <c>ref</c> expression, or <c>nameof</c>).
    /// </summary>
    private static string? FindUnsafeReferenceKind(IdentifierNameSyntax identifier)
    {
        SyntaxNode current = identifier;
        for (var depth = 0; depth < 6 && current.Parent != null; depth++)
        {
            var parent = current.Parent;
            switch (parent)
            {
                case RefExpressionSyntax:
                    return "a ref expression";

                case PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.AddressOfExpression)
                         && prefix.Operand == current:
                    return "an address-of expression";

                case ArgumentSyntax argument
                    when argument.Expression == current
                         && !argument.RefKindKeyword.IsKind(SyntaxKind.None):
                    return $"a {argument.RefKindKeyword.Text} argument";

                case InvocationExpressionSyntax invocation
                    when invocation.Expression is IdentifierNameSyntax
                    {
                        Identifier.ValueText: "nameof",
                    }:
                    return "nameof";
            }

            current = parent;
        }

        return null;
    }

    private static AssignmentExpressionSyntax? FindAssignmentTarget(
        IdentifierNameSyntax identifier)
    {
        SyntaxNode current = identifier;
        for (var depth = 0; depth < 6 && current.Parent != null; depth++)
        {
            if (current.Parent is AssignmentExpressionSyntax assignment
                && assignment.Left.Span.Contains(identifier.Span))
            {
                return assignment;
            }

            current = current.Parent;
        }

        return null;
    }

    // ── declaration rewrite ──────────────────────────────────────────────

    private static int BuildDeclaration(
        StructDeclarationSyntax declaration,
        TypePlan plan,
        SemanticModel model,
        out StructDeclarationSyntax replacement)
    {
        var endOfLine = FindEndOfLine(declaration);
        var indent = FindIndentation(declaration);
        var lifted = 0;

        var assignments = new Dictionary<AssignmentExpressionSyntax, AssignmentExpressionSyntax>(
            ReferenceEqualityComparer.Instance);
        foreach (var storage in plan.Storages)
        {
            if (storage.IsAutoProperty)
            {
                continue;
            }

            foreach (var assignment in storage.SimpleAssignments)
            {
                if (assignment.SyntaxTree != declaration.SyntaxTree
                    || !declaration.Span.Contains(assignment.Span))
                {
                    continue;
                }

                assignments[assignment] = RewriteAssignment(assignment, storage.MemoryName);
            }
        }

        var newMembers = new List<MemberDeclarationSyntax>(
            declaration.Members.Count + (plan.Storages.Count * 2) + 1);
        foreach (var member in declaration.Members)
        {
            var storage = FindStorage(plan, model, member);
            if (storage != null)
            {
                newMembers.Add(storage.IsAutoProperty
                    ? BuildMemoryFieldForProperty(member, storage, indent, endOfLine)
                    : BuildMemoryFieldForField(member, storage, indent, endOfLine));
                newMembers.Add(storage.IsAutoProperty
                    ? BuildLiftedProperty((PropertyDeclarationSyntax)member, storage)
                    : BuildLiftedFieldProperty((FieldDeclarationSyntax)member, storage));
                lifted++;
                continue;
            }

            if (assignments.Count > 0)
            {
                var memberAssignments = assignments.Keys
                    .Where(assignment =>
                        assignment.SyntaxTree == member.SyntaxTree
                        && member.Span.Contains(assignment.Span))
                    .ToList();
                if (memberAssignments.Count > 0)
                {
                    var map = new Dictionary<SyntaxNode, SyntaxNode>(
                        memberAssignments.Count, ReferenceEqualityComparer.Instance);
                    foreach (var assignment in memberAssignments)
                    {
                        map[assignment] = assignments[assignment];
                    }

                    newMembers.Add(member.ReplaceNodes(
                        map.Keys, (original, _) => map[original]));
                    continue;
                }
            }

            newMembers.Add(member);
        }

        if (IsHelperHost(declaration, plan))
        {
            newMembers.Add(BuildHelperMember(newMembers, indent, endOfLine));
        }

        replacement = RemoveRefModifier(declaration.WithMembers(SyntaxFactory.List(newMembers)));
        return lifted;
    }

    private static StorageLift? FindStorage(
        TypePlan plan, SemanticModel model, MemberDeclarationSyntax member)
    {
        ISymbol? symbol = member switch
        {
            FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1
                => model.GetDeclaredSymbol(field.Declaration.Variables[0]),
            PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
            _ => null,
        };

        return symbol == null
            ? null
            : plan.Storages.FirstOrDefault(storage =>
                SymbolEqualityComparer.Default.Equals(storage.Symbol, symbol));
    }

    private static bool IsHelperHost(StructDeclarationSyntax declaration, TypePlan plan)
    {
        var host = plan.HelperHost;
        return host != null
            && host.SyntaxTree == declaration.SyntaxTree
            && declaration.Span.Contains(host.Span);
    }

    private static AssignmentExpressionSyntax RewriteAssignment(
        AssignmentExpressionSyntax assignment, string memoryName)
    {
        var left = SyntaxFactory.IdentifierName(memoryName)
            .WithLeadingTrivia(assignment.Left.GetLeadingTrivia());
        var operatorToken = SyntaxFactory.Token(SyntaxKind.EqualsToken)
            .WithLeadingTrivia(SyntaxFactory.Space)
            .WithTrailingTrivia(assignment.OperatorToken.TrailingTrivia);

        // An untyped `default` is target-typed by the memory field itself and
        // cannot flow through the generic helper (TValue would be uninferrable).
        var right = assignment.Right.IsKind(SyntaxKind.DefaultLiteralExpression)
            ? assignment.Right
            : SyntaxFactory.InvocationExpression(
                    SyntaxFactory.IdentifierName(HelperName),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(assignment.Right.WithoutTrivia()))))
                .WithTrailingTrivia(assignment.Right.GetTrailingTrivia());

        return assignment
            .WithLeft(left)
            .WithOperatorToken(operatorToken)
            .WithRight(right);
    }

    private static FieldDeclarationSyntax BuildMemoryFieldForField(
        MemberDeclarationSyntax member,
        StorageLift storage,
        SyntaxTrivia indent,
        SyntaxTrivia endOfLine)
    {
        var field = (FieldDeclarationSyntax)member;
        var memoryType = SyntaxFactory.ParseTypeName(storage.MemoryTypeText);

        var declaration = SyntaxFactory.VariableDeclaration(memoryType)
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator(
                    CreateSpacedIdentifier(storage.MemoryName))));

        return SyntaxFactory.FieldDeclaration(declaration)
            .WithAttributeLists(field.AttributeLists)
            .WithModifiers(field.Modifiers)
            .WithLeadingTrivia(indent)
            .WithTrailingTrivia(endOfLine);
    }

    private static FieldDeclarationSyntax BuildMemoryFieldForProperty(
        MemberDeclarationSyntax member,
        StorageLift storage,
        SyntaxTrivia indent,
        SyntaxTrivia endOfLine)
    {
        var declaration = SyntaxFactory.VariableDeclaration(
                SyntaxFactory.ParseTypeName(storage.MemoryTypeText))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator(
                    CreateSpacedIdentifier(storage.MemoryName))));

        return SyntaxFactory.FieldDeclaration(declaration)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space)))
            .WithLeadingTrivia(indent)
            .WithTrailingTrivia(endOfLine);
    }

    private static PropertyDeclarationSyntax BuildLiftedFieldProperty(
        FieldDeclarationSyntax field, StorageLift storage)
    {
        var modifiers = SyntaxFactory.TokenList(field.Modifiers.Where(token =>
            !token.IsKind(SyntaxKind.ReadOnlyKeyword)));

        var spanType = SyntaxFactory.ParseTypeName(storage.SpanTypeText);

        var property = SyntaxFactory.PropertyDeclaration(
                spanType,
                CreateSpacedIdentifier(storage.SpanName))
            .WithModifiers(modifiers)
            .WithExpressionBody(CreateArrow(MemberAccessSpan(storage.MemoryName)))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return property
            .WithLeadingTrivia(field.GetLeadingTrivia())
            .WithTrailingTrivia(field.GetTrailingTrivia());
    }

    private static PropertyDeclarationSyntax BuildLiftedProperty(
        PropertyDeclarationSyntax property, StorageLift storage)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        foreach (var accessor in property.AccessorList!.Accessors)
        {
            var kind = accessor.Kind();
            if (kind == SyntaxKind.GetAccessorDeclaration)
            {
                accessors.Add(accessor
                    .WithBody(null)
                    .WithExpressionBody(CreateArrow(MemberAccessSpan(storage.MemoryName)))
                    .WithSemicolonToken(SemicolonWithOriginalTrailing(accessor)));
            }
            else if (kind is SyntaxKind.SetAccessorDeclaration
                     or SyntaxKind.InitAccessorDeclaration)
            {
                accessors.Add(accessor
                    .WithBody(null)
                    .WithExpressionBody(CreateArrow(ToMemoryAssignment(storage.MemoryName)))
                    .WithSemicolonToken(SemicolonWithOriginalTrailing(accessor)));
            }
            else
            {
                accessors.Add(accessor);
            }
        }

        var spanType = SyntaxFactory.ParseTypeName(storage.SpanTypeText);

        return property
            .WithType(spanType)
            .WithIdentifier(property.Identifier.WithLeadingTrivia(SyntaxFactory.Space))
            .WithAccessorList(property.AccessorList!.WithAccessors(
                SyntaxFactory.List(accessors)));
    }

    private static SyntaxToken SemicolonWithOriginalTrailing(
        AccessorDeclarationSyntax accessor)
        => SyntaxFactory.Token(SyntaxKind.SemicolonToken)
            .WithTrailingTrivia(accessor.SemicolonToken.TrailingTrivia);

    private static ArrowExpressionClauseSyntax CreateArrow(ExpressionSyntax expression)
        => SyntaxFactory.ArrowExpressionClause(expression)
            .WithArrowToken(SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken)
                .WithLeadingTrivia(SyntaxFactory.Space)
                .WithTrailingTrivia(SyntaxFactory.Space));

    private static SyntaxToken CreateSpacedIdentifier(string name)
        => SyntaxFactory.Identifier(
            SyntaxFactory.TriviaList(SyntaxFactory.Space),
            name,
            default(SyntaxTriviaList));

    private static ExpressionSyntax MemberAccessSpan(string memoryName)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(memoryName),
            SyntaxFactory.IdentifierName("Span"));

    private static ExpressionSyntax ToMemoryAssignment(string memoryName)
        => SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName(memoryName)
                .WithTrailingTrivia(SyntaxFactory.Space),
            SyntaxFactory.Token(SyntaxKind.EqualsToken)
                .WithTrailingTrivia(SyntaxFactory.Space),
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.IdentifierName(HelperName),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("value"))))));

    private static MemberDeclarationSyntax BuildHelperMember(
        IReadOnlyList<MemberDeclarationSyntax> members,
        SyntaxTrivia indent,
        SyntaxTrivia endOfLine)
    {
        const string helperText =
            "private static global::System.ReadOnlyMemory<TCs2jValue> " +
            "__cs2j_ToMemory<TCs2jValue>(" +
            "global::System.ReadOnlySpan<TCs2jValue> span) => " +
            "span == default ? default : " +
            "new global::System.ReadOnlyMemory<TCs2jValue>(span.ToArray());";

        var helper = SyntaxFactory.ParseMemberDeclaration(helperText)!;
        var needsLineBreak = members.Count > 0
            && !EndsWithEndOfLine(members[^1].GetTrailingTrivia());

        return helper
            .WithLeadingTrivia(needsLineBreak
                ? SyntaxFactory.TriviaList(endOfLine, indent)
                : SyntaxFactory.TriviaList(indent))
            .WithTrailingTrivia(endOfLine);
    }

    private static bool EndsWithEndOfLine(SyntaxTriviaList trivia)
        => trivia.Count > 0 && trivia[^1].IsKind(SyntaxKind.EndOfLineTrivia);

    private static SyntaxTrivia FindEndOfLine(StructDeclarationSyntax declaration)
    {
        foreach (var trivia in declaration.DescendantTrivia())
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                return trivia;
            }
        }

        return SyntaxFactory.EndOfLine("\r\n");
    }

    private static SyntaxTrivia FindIndentation(StructDeclarationSyntax declaration)
    {
        if (declaration.Members.Count > 0)
        {
            var leading = declaration.Members[0].GetLeadingTrivia();
            for (var index = leading.Count - 1; index >= 0; index--)
            {
                if (leading[index].IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    return leading[index];
                }
            }
        }

        return SyntaxFactory.Whitespace("    ");
    }

    /// <summary>
    /// Remove only the <c>ref</c> modifier token. When <c>ref</c> is the first
    /// modifier its leading trivia (indentation) moves to the struct keyword so
    /// the declaration keeps its layout.
    /// </summary>
    private static StructDeclarationSyntax RemoveRefModifier(
        StructDeclarationSyntax declaration)
    {
        var refToken = declaration.Modifiers.First(m => m.IsKind(SyntaxKind.RefKeyword));
        var isFirst = declaration.Modifiers[0] == refToken;
        var modifiers = declaration.Modifiers.Remove(refToken);

        var keyword = declaration.Keyword;
        if (isFirst)
        {
            keyword = keyword.WithLeadingTrivia(
                refToken.LeadingTrivia.Concat(keyword.LeadingTrivia));
        }

        return declaration.WithModifiers(modifiers).WithKeyword(keyword);
    }

    private static RefStructSpanFieldMemoryLiftDiagnostic CreatePreservedDiagnostic(
        StructDeclarationSyntax declaration,
        string reason,
        string filePath)
    {
        var line = 1;
        var column = 1;
        try
        {
            var span = declaration.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // Location unavailable: fall back to (1,1).
        }

        return new RefStructSpanFieldMemoryLiftDiagnostic(
            RefStructSpanFieldMemoryLiftSeverity.Warning,
            RefStructSpanFieldMemoryLiftDiagnosticCode.RSM001,
            $"ref struct '{declaration.Identifier.Text}' preserved (span field not lifted): " +
            $"{reason}.",
            filePath, line, column);
    }

    private static bool ParsesWithoutErrors(string code)
    {
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(
                code, new CSharpParseOptions(LanguageVersion.Latest));
            return !reparsed.GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }

    private static RefStructSpanFieldMemoryLiftResult Rollback(
        string sourceCode,
        RefStructSpanFieldMemoryLiftStatistics statistics,
        IReadOnlyList<RefStructSpanFieldMemoryLiftDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };

    // ── models ───────────────────────────────────────────────────────────

    private sealed record TypePlan(
        IReadOnlyList<StorageLift> Storages,
        string? RejectReason,
        SyntaxReference? HelperHost)
    {
        public static TypePlan Reject(string reason)
            => new(Array.Empty<StorageLift>(), reason, null);
    }

    private sealed record StorageLift(
        ISymbol Symbol,
        string SpanName,
        string MemoryName,
        INamedTypeSymbol SpanType,
        bool IsAutoProperty,
        IReadOnlyList<AssignmentExpressionSyntax> SimpleAssignments)
    {
        public string SpanTypeText =>
            $"global::System.ReadOnlySpan<{ElementTypeText}>";

        public string MemoryTypeText =>
            $"global::System.ReadOnlyMemory<{ElementTypeText}>";

        private string ElementTypeText =>
            SpanType.TypeArguments[0]
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private sealed class FieldReferenceScan
    {
        public string? UnsafeReason { get; set; }
        public bool NonSimpleAssignment { get; set; }
        public List<AssignmentExpressionSyntax> SimpleAssignments { get; } = new();
    }
}
