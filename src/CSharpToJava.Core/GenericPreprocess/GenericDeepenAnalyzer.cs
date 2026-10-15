using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

public enum GenericDeepenParamKind { ClassTypeParameter, MethodTypeParameter }

/// <summary>Type parameter referenced by (kind, ordinal) so family members with renamed parameters stay comparable.</summary>
public sealed record GenericDeepenParamRef(GenericDeepenParamKind Kind, int Ordinal, string Name)
    : IComparable<GenericDeepenParamRef>
{
    public int CompareTo(GenericDeepenParamRef? other)
    {
        if (other == null) return 1;
        var kind = Kind.CompareTo(other.Kind);
        return kind != 0 ? kind : Ordinal.CompareTo(other.Ordinal);
    }
}

/// <summary>Anchor plan for one method: the ordered type parameters whose runtime tokens the Java side needs.</summary>
public sealed class TokenAnchorPlan
{
    public required IMethodSymbol Method { get; init; }
    public required MethodDeclarationSyntax Declaration { get; init; }
    public required IReadOnlyList<GenericDeepenParamRef> Params { get; init; }
    /// <summary>Member without a body (abstract/interface): materialize a stub body carrying typeof statements (E10 precedent).</summary>
    public bool MaterializeBody { get; init; }
    public bool IsInterfaceMember { get; init; }
    public bool FromFamilySync { get; init; }
    public string FilePath => Declaration.SyntaxTree.FilePath;
}

/// <summary>One call site of a hoisted static method (GD-STATIC).</summary>
public sealed class StaticMethodCallSitePlan
{
    public required InvocationExpressionSyntax Invocation { get; init; }
    public required IReadOnlyList<string> TypeArgumentTexts { get; init; }
}

/// <summary>
/// Token anchor inside a nested closure (lambda/anonymous method/local function):
/// GD-NEST requires every layer on the chain between a token-need site and the
/// type parameter's declaring scope to carry its own <c>System.Type</c> anchor,
/// because the Java side lowers each closure into its own method/display class.
/// </summary>
public sealed class ClosureAnchorPlan
{
    public required SyntaxNode Closure { get; init; }
    public required IReadOnlyList<GenericDeepenParamRef> Params { get; init; }
    public string FilePath => Closure.SyntaxTree!.FilePath;
}

/// <summary>Static method referencing enclosing class type parameters, hoisted into a generic method (GD-STATIC).</summary>
public sealed class StaticMethodPlan
{
    public required IMethodSymbol Method { get; init; }
    public required MethodDeclarationSyntax Declaration { get; init; }
    /// <summary>Class params hoisted under their own names (same-name shadowing keeps body text untouched).</summary>
    public required IReadOnlyList<GenericDeepenParamRef> Params { get; init; }
    public required IReadOnlyList<string> ConstraintTexts { get; init; }
    public required IReadOnlyList<StaticMethodCallSitePlan> CallSites { get; init; }
    public string FilePath => Declaration.SyntaxTree.FilePath;
}

/// <summary>Whole-compilation analysis result: plans keyed by symbol, sorted by (file, span).</summary>
public sealed class GenericDeepenAnalysis
{
    public required IReadOnlyList<TokenAnchorPlan> Anchors { get; init; }
    public required IReadOnlyList<ClosureAnchorPlan> ClosureAnchors { get; init; }
    public required IReadOnlyList<TypeParamPatternPlan> Patterns { get; init; }
    public required IReadOnlyList<CatchTypeParamPlan> Catches { get; init; }
    public required IReadOnlyList<StaticFieldPlan> StaticFields { get; init; }
    public required IReadOnlyList<StaticMethodPlan> StaticMethods { get; init; }
    public required IReadOnlyList<GenericDeepenDiagnostic> Diagnostics { get; init; }
    public required GenericDeepenStatistics Statistics { get; init; }

    public static GenericDeepenAnalysis Empty { get; } = new()
    {
        Anchors = Array.Empty<TokenAnchorPlan>(),
        ClosureAnchors = Array.Empty<ClosureAnchorPlan>(),
        Patterns = Array.Empty<TypeParamPatternPlan>(),
        Catches = Array.Empty<CatchTypeParamPlan>(),
        StaticFields = Array.Empty<StaticFieldPlan>(),
        StaticMethods = Array.Empty<StaticMethodPlan>(),
        Diagnostics = Array.Empty<GenericDeepenDiagnostic>(),
        Statistics = new GenericDeepenStatistics(),
    };
}

/// <summary>Liftable `if (x is T v)` statement: lowered to single-evaluation if-form (GD-ISAS-B).</summary>
public sealed class TypeParamPatternPlan
{
    public required IsPatternExpressionSyntax Pattern { get; init; }
    public required IfStatementSyntax If { get; init; }
    public required GenericDeepenParamRef Param { get; init; }
    public required string VariableName { get; init; }
    public required string TempName { get; init; }
    public required bool IsDiscard { get; init; }
}

/// <summary>`catch (T e)` clause lowered to a filtered catch over Exception (GD-CATCH).</summary>
public sealed class CatchTypeParamPlan
{
    public required CatchClauseSyntax Clause { get; init; }
    public required GenericDeepenParamRef Param { get; init; }
    public required string? VariableName { get; init; }
    public required string ExceptionVariableName { get; init; }
}

/// <summary>One reference site of a static generic field (GD-FIELD).</summary>
public sealed class StaticFieldUsagePlan
{
    public required SyntaxNode Node { get; init; }
    public required StaticFieldPlan Plan { get; init; }
    public required bool IsAssignmentTarget { get; init; }
    public required IReadOnlyList<string> TypeArgumentTexts { get; init; }
}

/// <summary>A static field referencing enclosing type parameters, replaced by holder accessors (GD-FIELD).</summary>
public sealed class StaticFieldPlan
{
    public required IFieldSymbol Field { get; init; }
    public required FieldDeclarationSyntax Declaration { get; init; }
    public required VariableDeclaratorSyntax Declarator { get; init; }
    public required string HolderTypeName { get; init; }
    public required string? HolderNamespace { get; init; }
    public required string FieldName { get; init; }
    public required IReadOnlyList<GenericDeepenParamRef> Params { get; init; }
    public required IReadOnlyList<string> ConstraintTexts { get; init; }
    public required string FieldTypeText { get; init; }
    public required string? InitializerText { get; init; }
    public bool IsSingletonForm { get; init; }
    public bool NeedsSetter { get; init; }
    public required IReadOnlyList<StaticFieldUsagePlan> Usages { get; set; }
    public string FilePath => Declaration.SyntaxTree.FilePath;
}

/// <summary>
/// Whole-compilation analyzer for generic-deepen (C02): collects per-method token needs
/// (GD-ANCHOR L0), override/interface family edges (L1) and forwarding call edges (L1),
/// then produces symbol-level plans with deterministic ordering.
/// </summary>
public sealed class GenericDeepenAnalyzer
{
    private readonly Compilation _compilation;
    private readonly GenericDeepenOptions _options;
    private readonly List<GenericDeepenDiagnostic> _diagnostics = new();
    private readonly HashSet<string> _reportedFamilyGaps = new(StringComparer.Ordinal);

    private readonly Dictionary<IMethodSymbol, MethodDeclarationSyntax> _methodDecls =
        new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, SortedSet<GenericDeepenParamRef>> _needs =
        new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, List<ForwardEdge>> _forwardEdges =
        new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, List<IMethodSymbol>> _families =
        new(SymbolEqualityComparer.Default);

    /// <summary>GD-NEST: anchor needs keyed by the closure syntax node (lambda/anonymous method/local function).</summary>
    private readonly Dictionary<SyntaxNode, SortedSet<GenericDeepenParamRef>> _closureNeeds =
        new();

    /// <summary>Local-function syntax node that declares a type parameter (null for method/class-declared parameters).</summary>
    private readonly Dictionary<ITypeParameterSymbol, LocalFunctionStatementSyntax?> _closureOwners =
        new(SymbolEqualityComparer.Default);

    private readonly List<TypeParamPatternPlan> _patterns = new();
    private readonly List<CatchTypeParamPlan> _catches = new();
    private int _patternTempCounter;

    private readonly record struct ForwardEdge(
        IMethodSymbol Callee,
        int CalleeParamOrdinal,
        GenericDeepenParamRef CallerParam,
        InvocationExpressionSyntax Invocation);

    private sealed class ParamRefComparer : IComparer<GenericDeepenParamRef>
    {
        public static readonly ParamRefComparer Instance = new();

        public int Compare(GenericDeepenParamRef? x, GenericDeepenParamRef? y)
            => (x ?? throw new ArgumentNullException(nameof(x))).CompareTo(y);
    }

    /// <summary>
    /// Closure layers can simultaneously carry parameters that are distinct by name
    /// but share a (kind, ordinal) slot — e.g. method <c>F&lt;T&gt;</c>'s T #0 and nested
    /// local function <c>A&lt;U&gt;</c>'s U #0 inside one closure set. Method-level
    /// family sync intentionally identifies parameters by (kind, ordinal) alone;
    /// closure plans must not collapse same-ordinal differently-named parameters.
    /// </summary>
    private sealed class NamedParamRefComparer : IComparer<GenericDeepenParamRef>
    {
        public static readonly NamedParamRefComparer Instance = new();

        public int Compare(GenericDeepenParamRef? x, GenericDeepenParamRef? y)
        {
            var baseCompare = ParamRefComparer.Instance.Compare(x, y);
            return baseCompare != 0 ? baseCompare : string.CompareOrdinal(x!.Name, y!.Name);
        }
    }

    public GenericDeepenAnalyzer(Compilation compilation, GenericDeepenOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    public GenericDeepenAnalysis Analyze()
    {
        if (!_options.RewriteTypeOfAnchors && !_options.RewriteIsAsPatterns && !_options.RewriteCatchTypeParam
            && !_options.RewriteStaticFieldHolders && !_options.RewriteStaticMembers)
        {
            return GenericDeepenAnalysis.Empty;
        }

        foreach (var tree in _compilation.SyntaxTrees)
        {
            WalkTree(tree);
        }

        BuildFamilies();
        PropagateNeeds();
        CollectStaticFieldPlans();
        CollectStaticMethodPlans();

        var anchors = new List<TokenAnchorPlan>();
        foreach (var (method, needs) in _needs)
        {
            if (needs.Count == 0) continue;
            if (!_methodDecls.TryGetValue(method, out var declaration)) continue;

            var isInterface = method.ContainingType?.TypeKind == TypeKind.Interface;
            var hasBody = declaration.Body != null || declaration.ExpressionBody != null;
            var isPartial = declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
            if (isPartial && !hasBody)
            {
                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.Anchor,
                    GenericDeepenCodes.StaticFamilyNotClosable,
                    $"Partial method '{method.ToDisplayString()}' needs a token anchor but has no implementation part; skipped.",
                    declaration.SyntaxTree.FilePath,
                    declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }

            anchors.Add(new TokenAnchorPlan
            {
                Method = method,
                Declaration = declaration,
                Params = needs.ToList(),
                MaterializeBody = !hasBody,
                IsInterfaceMember = isInterface,
            });
        }

        anchors.Sort((a, b) => string.CompareOrdinal(a.FilePath, b.FilePath) != 0
            ? string.CompareOrdinal(a.FilePath, b.FilePath)
            : a.Declaration.SpanStart.CompareTo(b.Declaration.SpanStart));

        var closureAnchors = new List<ClosureAnchorPlan>();
        foreach (var (closure, needs) in _closureNeeds)
        {
            if (needs.Count == 0) continue;
            closureAnchors.Add(new ClosureAnchorPlan
            {
                Closure = closure,
                Params = needs.ToList(),
            });
        }

        closureAnchors.Sort((a, b) => string.CompareOrdinal(a.FilePath, b.FilePath) != 0
            ? string.CompareOrdinal(a.FilePath, b.FilePath)
            : a.Closure.SpanStart.CompareTo(b.Closure.SpanStart));
        _patterns.Sort((a, b) => string.CompareOrdinal(a.If.SyntaxTree.FilePath, b.If.SyntaxTree.FilePath) != 0
            ? string.CompareOrdinal(a.If.SyntaxTree.FilePath, b.If.SyntaxTree.FilePath)
            : a.If.SpanStart.CompareTo(b.If.SpanStart));
        _catches.Sort((a, b) => string.CompareOrdinal(a.Clause.SyntaxTree.FilePath, b.Clause.SyntaxTree.FilePath) != 0
            ? string.CompareOrdinal(a.Clause.SyntaxTree.FilePath, b.Clause.SyntaxTree.FilePath)
            : a.Clause.SpanStart.CompareTo(b.Clause.SpanStart));

        return new GenericDeepenAnalysis
        {
            Anchors = anchors,
            ClosureAnchors = closureAnchors,
            Patterns = _patterns,
            Catches = _catches,
            StaticFields = _staticFields,
            StaticMethods = _staticMethods,
            Diagnostics = EscalateDiagnostics(_diagnostics, _options),
            Statistics = _analysisStats,
        };
    }

    /// <summary>
    /// Strict escalation (spec §6): every Warning becomes Error; the expression-tree
    /// CS2J1110 trace is Info→Warning under Skip policy (Warning→Error under the
    /// Error policy). GD001 Info details stay Info.
    /// </summary>
    private static List<GenericDeepenDiagnostic> EscalateDiagnostics(
        List<GenericDeepenDiagnostic> diagnostics,
        GenericDeepenOptions options)
    {
        if (!options.Strict)
        {
            return diagnostics;
        }

        var escalated = new List<GenericDeepenDiagnostic>(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            var severity = diagnostic.Severity switch
            {
                GenericDeepenSeverity.Warning => GenericDeepenSeverity.Error,
                GenericDeepenSeverity.Info when diagnostic.Code == GenericDeepenCodes.ExpressionTreeSite =>
                    GenericDeepenSeverity.Warning,
                _ => diagnostic.Severity,
            };
            escalated.Add(severity == diagnostic.Severity ? diagnostic : diagnostic with { Severity = severity });
        }

        return escalated;
    }

    // ---- shared symbol index (GD-FIELD / GD-STATIC analysis) ----

    private readonly Dictionary<IMethodSymbol, List<InvocationSite>> _invocationsByMethod =
        new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, List<IdentifierSite>> _identifiersBySymbol =
        new(SymbolEqualityComparer.Default);
    private bool _symbolIndexBuilt;
    private readonly HashSet<string> _fieldCandidateNames = new(StringComparer.Ordinal);

    private readonly record struct InvocationSite(InvocationExpressionSyntax Invocation, SemanticModel Model);
    private readonly record struct IdentifierSite(IdentifierNameSyntax Identifier, SemanticModel Model);

    /// <summary>
    /// One-time per-tree scan that binds every invocation and identifier with its
    /// declaring model, keyed by original definition. It replaces the repeated
    /// whole-tree <c>GetSymbolInfo</c> scans in the field/static-method collectors
    /// while preserving the original tree-then-source visitation order.
    /// </summary>
    private void EnsureSymbolIndex()
    {
        if (_symbolIndexBuilt)
        {
            return;
        }

        _symbolIndexBuilt = true;

        // Only an identifier whose simple name matches a candidate method or field
        // can ever be queried (the original code pre-filtered on that name before
        // binding). Invocations are already bound during WalkTree, so the binding
        // below is served from the semantic-model cache and needs no filter.
        var identifierNameFilter = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in _methodDecls.Keys)
        {
            identifierNameFilter.Add(method.Name);
        }

        foreach (var name in _fieldCandidateNames)
        {
            identifierNameFilter.Add(name);
        }

        var trees = _compilation.SyntaxTrees.ToList();
        var count = trees.Count;
        var models = new SemanticModel[count];
        for (var i = 0; i < count; i++)
        {
            models[i] = _compilation.GetSemanticModel(trees[i]);
        }

        var perTreeInvocations = new List<(IMethodSymbol Symbol, InvocationExpressionSyntax Invocation, SemanticModel Model)>[count];
        var perTreeIdentifiers = new List<(ISymbol Symbol, IdentifierNameSyntax Identifier, SemanticModel Model)>[count];

        System.Threading.Tasks.Parallel.For(0, count, i =>
        {
            var model = models[i];
            var invocations = new List<(IMethodSymbol, InvocationExpressionSyntax, SemanticModel)>();
            var identifiers = new List<(ISymbol, IdentifierNameSyntax, SemanticModel)>();
            foreach (var node in trees[i].GetRoot().DescendantNodes())
            {
                if (node is InvocationExpressionSyntax invocation)
                {
                    if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol resolvedMethod)
                    {
                        invocations.Add((resolvedMethod.OriginalDefinition, invocation, model));
                    }
                }
                else if (node is IdentifierNameSyntax identifier
                    && identifierNameFilter.Contains(identifier.Identifier.ValueText)
                    && model.GetSymbolInfo(identifier).Symbol is { } resolvedSymbol)
                {
                    identifiers.Add((resolvedSymbol.OriginalDefinition, identifier, model));
                }
            }

            perTreeInvocations[i] = invocations;
            perTreeIdentifiers[i] = identifiers;
        });

        // Append per-tree results in SyntaxTrees order so lookups stay deterministic.
        for (var i = 0; i < count; i++)
        {
            foreach (var (symbol, invocation, model) in perTreeInvocations[i])
            {
                if (!_invocationsByMethod.TryGetValue(symbol, out var invocationList))
                {
                    invocationList = new List<InvocationSite>();
                    _invocationsByMethod[symbol] = invocationList;
                }

                invocationList.Add(new InvocationSite(invocation, model));
            }

            foreach (var (symbol, identifier, model) in perTreeIdentifiers[i])
            {
                if (!_identifiersBySymbol.TryGetValue(symbol, out var identifierList))
                {
                    identifierList = new List<IdentifierSite>();
                    _identifiersBySymbol[symbol] = identifierList;
                }

                identifierList.Add(new IdentifierSite(identifier, model));
            }
        }
    }

    // ---- GD-FIELD: static generic fields ----

    private readonly List<StaticFieldPlan> _staticFields = new();

    private void CollectStaticFieldPlans()
    {
        if (!_options.RewriteStaticFieldHolders)
        {
            return;
        }

        // Phase A: candidate declarations.
        var candidates = new List<(IFieldSymbol Field, FieldDeclarationSyntax Declaration, VariableDeclaratorSyntax Declarator,
            List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> Visible, List<GenericDeepenParamRef> Referenced)>();
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var field in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (!field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) continue;

                if (field.Declaration.Variables.Count != 1)
                {
                    _diagnostics.Add(new GenericDeepenDiagnostic(
                        GenericDeepenSeverity.Warning, GenericDeepenRule.StaticField, GenericDeepenCodes.StaticFieldNotRewritable,
                        $"Static generic field declaration '{field.Declaration.Variables[0].Identifier.ValueText}' has multiple declarators; "
                        + "v1 only rewrites single-declarator fields, the declaration is left in place.",
                        tree.FilePath, field.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                    continue;
                }

                var declarator = field.Declaration.Variables[0];

                if (field.AttributeLists.SelectMany(l => l.Attributes)
                    .Any(a => a.Name.ToString() is "ThreadStatic" or "ThreadStaticAttribute"
                        or "global::System.ThreadStatic" or "global::System.ThreadStaticAttribute"))
                {
                    _diagnostics.Add(new GenericDeepenDiagnostic(
                        GenericDeepenSeverity.Info, GenericDeepenRule.StaticField, GenericDeepenCodes.Info,
                        $"[ThreadStatic] field '{field.Declaration.Variables[0].Identifier.ValueText}' is owned by the thread-static pass (C09); skipped here.",
                        tree.FilePath, field.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                    continue;
                }

                if (model.GetDeclaredSymbol(declarator) is not IFieldSymbol fieldSymbol) continue;
                var visible = CollectVisibleClassParams(fieldSymbol);
                if (visible.Count == 0) continue;

                var referenced = new List<GenericDeepenParamRef>();
                CollectTypeReferences(fieldSymbol.Type, visible, referenced);
                if (declarator.Initializer?.Value is { } initializer)
                {
                    foreach (var identifier in initializer.DescendantNodes().OfType<IdentifierNameSyntax>())
                    {
                        if (model.GetSymbolInfo(identifier).Symbol is ITypeParameterSymbol tp)
                        {
                            var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, tp));
                            if (match.Ref != null && !referenced.Contains(match.Ref))
                            {
                                referenced.Add(match.Ref);
                            }
                        }
                    }
                }

                if (referenced.Count == 0) continue;
                referenced.Sort(ParamRefComparer.Instance);
                candidates.Add((fieldSymbol, field, declarator, visible, referenced));
            }
        }

        foreach (var candidate in candidates)
        {
            _fieldCandidateNames.Add(candidate.Field.Name);
        }

        EnsureSymbolIndex();

        // Phase B: usage index (cross-tree), served by the prebuilt symbol index.
        foreach (var candidate in candidates)
        {
            var usages = new List<(SyntaxNode Node, bool IsAssignmentTarget, IReadOnlyList<string> TypeArgumentTexts)>();
            var excluded = false;
            if (_identifiersBySymbol.TryGetValue(candidate.Field.OriginalDefinition, out var usageSites))
            {
                foreach (var site in usageSites)
                {
                    var identifier = site.Identifier;
                    if (identifier.Identifier.ValueText != candidate.Field.Name) continue;
                    var model = site.Model;

                    if (IsWithinNameOf(identifier)) continue;

                    if (IsWithinExpressionTree(model, identifier))
                    {
                        _diagnostics.Add(new GenericDeepenDiagnostic(
                            GenericDeepenSeverity.Warning, GenericDeepenRule.StaticField, GenericDeepenCodes.ExpressionTreeSite,
                            $"Static generic field '{candidate.Field.Name}' is referenced inside an expression-tree lambda; the field cannot be rewritten.",
                            identifier.SyntaxTree.FilePath, identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                        excluded = true;
                        break;
                    }

                    var (classification, typeArgs) = ClassifyUsage(identifier, model, candidate);
                    if (classification == UsageClassification.Exclude)
                    {
                        _diagnostics.Add(new GenericDeepenDiagnostic(
                            GenericDeepenSeverity.Warning, GenericDeepenRule.StaticField, GenericDeepenCodes.StaticFieldNotRewritable,
                            $"Usage of static generic field '{candidate.Field.Name}' (compound assignment, ref/out pass, or unresolvable construction) cannot be rewritten; the field is left in place.",
                            identifier.SyntaxTree.FilePath, identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                        excluded = true;
                        break;
                    }

                    usages.Add((identifier, classification == UsageClassification.AssignmentTarget, typeArgs ?? Array.Empty<string>()));
                }
            }

            if (excluded)
            {
                continue;
            }

            var declaration = candidate.Declaration;
            var isReadonly = candidate.Field.IsReadOnly;
            var hasInitializer = candidate.Declarator.Initializer != null;
            var hasAssignments = usages.Any(u => u.IsAssignmentTarget);
            var initializerIsSimple = hasInitializer && InitializerReferencesNoStaticGenericMembers(
                _compilation.GetSemanticModel(declaration.SyntaxTree), candidate.Declarator.Initializer!.Value);
            var isSingletonForm = isReadonly && !hasAssignments && hasInitializer && initializerIsSimple;

            var plan = new StaticFieldPlan
            {
                Field = candidate.Field,
                Declaration = declaration,
                Declarator = candidate.Declarator,
                HolderTypeName = GenericDeepenSyntaxFacts.HolderTypeName(candidate.Field.ContainingType.OriginalDefinition.MetadataName),
                HolderNamespace = candidate.Field.ContainingNamespace is { IsGlobalNamespace: false } ns
                    ? ns.ToDisplayString()
                    : null,
                FieldName = candidate.Field.Name,
                Params = candidate.Referenced,
                ConstraintTexts = CollectConstraintTexts(declaration, candidate.Referenced),
                FieldTypeText = declaration.Declaration.Type.ToString(),
                InitializerText = hasInitializer
                    ? RenderInitializerText(candidate.Declarator.Initializer!.Value, candidate.Referenced)
                    : null,
                IsSingletonForm = isSingletonForm,
                NeedsSetter = !isReadonly,
                Usages = Array.Empty<StaticFieldUsagePlan>(),
            };
            plan.Usages = usages
                .Select(u => new StaticFieldUsagePlan
                {
                    Node = u.Node,
                    Plan = plan,
                    IsAssignmentTarget = u.IsAssignmentTarget,
                    TypeArgumentTexts = u.TypeArgumentTexts,
                })
                .ToList();
            _staticFields.Add(plan);
        }

        _staticFields.Sort((a, b) => string.CompareOrdinal(a.FilePath, b.FilePath) != 0
            ? string.CompareOrdinal(a.FilePath, b.FilePath)
            : a.Declaration.SpanStart.CompareTo(b.Declaration.SpanStart));
    }

    private enum UsageClassification { Read, AssignmentTarget, Exclude }

    private static List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> CollectVisibleClassParams(ISymbol symbol)
    {
        var result = new List<(ITypeParameterSymbol, GenericDeepenParamRef)>();
        var type = symbol.ContainingType?.OriginalDefinition;
        while (type != null)
        {
            foreach (var tp in type.TypeParameters)
            {
                result.Add((tp, new GenericDeepenParamRef(GenericDeepenParamKind.ClassTypeParameter, tp.Ordinal, tp.Name)));
            }

            type = type.ContainingType?.OriginalDefinition;
        }

        return result;
    }

    private static void CollectTypeReferences(
        ITypeSymbol type,
        List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> visible,
        List<GenericDeepenParamRef> referenced)
    {
        switch (type)
        {
            case ITypeParameterSymbol tp:
                var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, tp));
                if (match.Ref != null && !referenced.Contains(match.Ref))
                {
                    referenced.Add(match.Ref);
                }

                break;
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments)
                {
                    CollectTypeReferences(argument, visible, referenced);
                }

                break;
            case IArrayTypeSymbol array:
                CollectTypeReferences(array.ElementType, visible, referenced);
                break;
        }
    }

    private static bool IsWithinNameOf(IdentifierNameSyntax identifier)
        => identifier.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax { Expression: IdentifierNameSyntax invoked } } }
            && invoked.Identifier.ValueText == "nameof";

    /// <summary>
    /// Shared forbidden-zone predicate (catalog §4 risk 8): also covers lambdas
    /// quoted via an Expression&lt;&gt; parameter and nested Func lambdas inside
    /// a quoted lambda, not just directly-converted Expression lambdas.
    /// </summary>
    private static bool IsWithinExpressionTree(SemanticModel model, SyntaxNode node)
        => CSharpToJava.Core.Analysis.ExpressionTreeFacts.IsInsideQuotedExpressionLambda(model, node);

    private (UsageClassification Classification, IReadOnlyList<string> TypeArgs) ClassifyUsage(
        IdentifierNameSyntax identifier,
        SemanticModel model,
        (IFieldSymbol Field, FieldDeclarationSyntax Declaration, VariableDeclaratorSyntax Declarator,
            List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> Visible, List<GenericDeepenParamRef> Referenced) candidate)
    {
        // Compound / incremented / ref-out usages are not rewritable in v1.
        if (identifier.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PostIncrementExpression
                or (int)SyntaxKind.PreDecrementExpression or (int)SyntaxKind.PostDecrementExpression
            })
        {
            return (UsageClassification.Exclude, Array.Empty<string>());
        }

        var assignment = identifier.Ancestors().OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(a => a.Left.Span.Contains(identifier.Span));
        if (assignment != null)
        {
            var isSimpleStatementAssignment = assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assignment.Parent is ExpressionStatementSyntax
                && (assignment.Left == identifier
                    || assignment.Left is MemberAccessExpressionSyntax member && member.Name == identifier);
            if (!isSimpleStatementAssignment)
            {
                return (UsageClassification.Exclude, Array.Empty<string>());
            }

            var assignmentArgs = RenderTypeArguments(identifier, model, candidate);
            return assignmentArgs == null
                ? (UsageClassification.Exclude, Array.Empty<string>())
                : (UsageClassification.AssignmentTarget, assignmentArgs);
        }

        if (identifier.Ancestors().OfType<PrefixUnaryExpressionSyntax>().Any(p => p.Operand.Span.Contains(identifier.Span)
                && (p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression)))
            || identifier.Ancestors().OfType<PostfixUnaryExpressionSyntax>().Any(p => p.Operand.Span.Contains(identifier.Span)
                && (p.IsKind(SyntaxKind.PostIncrementExpression) || p.IsKind(SyntaxKind.PostDecrementExpression))))
        {
            return (UsageClassification.Exclude, Array.Empty<string>());
        }

        if (identifier.Ancestors().OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left.Span.Contains(identifier.Span) && !a.IsKind(SyntaxKind.SimpleAssignmentExpression)))
        {
            return (UsageClassification.Exclude, Array.Empty<string>());
        }

        if (identifier.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not (int)SyntaxKind.None })
        {
            return (UsageClassification.Exclude, Array.Empty<string>());
        }

        var typeArgs = RenderTypeArguments(identifier, model, candidate);
        return typeArgs == null
            ? (UsageClassification.Exclude, Array.Empty<string>())
            : (UsageClassification.Read, typeArgs);
    }

    /// <summary>
    /// Renders the accessor's type arguments for one usage: receiver construction
    /// (C&lt;int&gt;.F → "int") or the enclosing declaration's own parameter names (bare F → "T").
    /// Null means the construction could not be resolved.
    /// </summary>
    private IReadOnlyList<string>? RenderTypeArguments(
        IdentifierNameSyntax identifier,
        SemanticModel model,
        (IFieldSymbol Field, FieldDeclarationSyntax Declaration, VariableDeclaratorSyntax Declarator,
            List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> Visible, List<GenericDeepenParamRef> Referenced) candidate)
    {
        var containingTypeName = candidate.Field.ContainingType.OriginalDefinition.Name;
        var texts = new List<string>();

        if (identifier.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == identifier)
        {
            var generic = memberAccess.Expression.DescendantNodesAndSelf()
                .OfType<GenericNameSyntax>()
                .FirstOrDefault(g => g.Identifier.ValueText == containingTypeName);
            if (generic == null)
            {
                return null;
            }

            foreach (var param in candidate.Referenced)
            {
                texts.Add(generic.TypeArgumentList.Arguments[param.Ordinal].ToString());
            }

            return texts;
        }

        // Bare identifier: inside the declaring type's own declaration(s).
        foreach (var ancestor in identifier.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            if (ancestor.Identifier.ValueText != containingTypeName || ancestor.TypeParameterList == null)
            {
                continue;
            }

            foreach (var param in candidate.Referenced)
            {
                texts.Add(ancestor.TypeParameterList.Parameters[param.Ordinal].Identifier.ValueText);
            }

            return texts;
        }

        return null;
    }

    private static IReadOnlyList<string> CollectConstraintTexts(SyntaxNode declaration, IEnumerable<GenericDeepenParamRef> referenced)
    {
        var names = referenced.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var texts = new List<string>();
        foreach (var ancestor in declaration.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            foreach (var clause in ancestor.ConstraintClauses)
            {
                if (clause.Name.Identifier.ValueText is { } name && names.Contains(name))
                {
                    texts.Add(clause.ToString());
                }
            }
        }

        return texts;
    }

    private static bool InitializerReferencesNoStaticGenericMembers(SemanticModel model, ExpressionSyntax initializer)
    {
        foreach (var identifier in initializer.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(identifier).Symbol is { IsStatic: true } symbol
                && symbol.ContainingType is { IsGenericType: true })
            {
                return false;
            }
        }

        return true;
    }

    private static string RenderInitializerText(ExpressionSyntax initializer, IReadOnlyList<GenericDeepenParamRef> referenced)
    {
        var text = initializer.ToString();
        foreach (var param in referenced)
        {
            // typeof(T) → the accessor's token parameter; other T references keep
            // binding to the accessor's own type parameter (same-name shadowing).
            text = System.Text.RegularExpressions.Regex.Replace(
                text, $@"typeof\(\s*{param.Name}\s*\)", $"__cs2j_token_{param.Name}");
        }

        return text;
    }

    // ---- GD-STATIC: static method type-parameter hoisting ----

    private readonly List<StaticMethodPlan> _staticMethods = new();

    private void CollectStaticMethodPlans()
    {
        if (!_options.RewriteStaticMembers)
        {
            return;
        }

        EnsureSymbolIndex();

        ReportStaticPropertyGaps();

        // Candidate pool: static ordinary methods in generic types without own type parameters.
        var pool = new List<(IMethodSymbol Method, MethodDeclarationSyntax Declaration,
            List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> Visible)>();
        foreach (var (method, declaration) in _methodDecls)
        {
            if (method.MethodKind != MethodKind.Ordinary || !method.IsStatic) continue;

            var visible = CollectVisibleClassParams(method);
            if (visible.Count == 0) continue;

            if (method.TypeParameters.Length > 0)
            {
                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.StaticMember, GenericDeepenCodes.StaticFamilyNotClosable,
                    $"Static method '{method.ToDisplayString()}' has its own type parameters on top of the enclosing class parameters; hoisting would collide, the method is left in place.",
                    declaration.SyntaxTree.FilePath,
                    declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }

            var isStaticAbstract = declaration.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.AbstractKeyword) || m.IsKind(SyntaxKind.VirtualKeyword));
            if (method.ContainingType.TypeKind == TypeKind.Interface || isStaticAbstract)
            {
                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.StaticMember, GenericDeepenCodes.StaticFamilyNotClosable,
                    $"Static member '{method.ToDisplayString()}' is interface/static-abstract; hoisting is not supported there, the method is left in place.",
                    declaration.SyntaxTree.FilePath,
                    declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }

            var isExtern = declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword));
            if (isExtern || declaration.Body == null && declaration.ExpressionBody == null)
            {
                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.StaticMember, GenericDeepenCodes.StaticFamilyNotClosable,
                    $"Static method '{method.ToDisplayString()}' has no implementable body (extern/partial definition); hoisting is skipped.",
                    declaration.SyntaxTree.FilePath,
                    declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }

            if (HasMethodGroupReference(method))
            {
                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.StaticMember, GenericDeepenCodes.StaticFamilyNotClosable,
                    $"Static method '{method.ToDisplayString()}' is referenced as a method group (delegate conversion) somewhere; hoisting would change the delegate shape, the method is left in place.",
                    declaration.SyntaxTree.FilePath,
                    declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }

            pool.Add((method, declaration, visible));
        }

        // Fixpoint: a static method referencing class params (signature, body, or a call
        // into an already-planned hoisted method with a class-param type argument) gets hoisted.
        var planned = new Dictionary<IMethodSymbol, SortedSet<GenericDeepenParamRef>>(SymbolEqualityComparer.Default);
        bool changed;
        do
        {
            changed = false;
            foreach (var (method, declaration, visible) in pool)
            {
                if (planned.ContainsKey(method)) continue;

                var referenced = new List<GenericDeepenParamRef>();
                CollectTypeReferences(method.ReturnType, visible, referenced);
                foreach (var parameter in method.Parameters)
                {
                    CollectTypeReferences(parameter.Type, visible, referenced);
                }

                var model = _compilation.GetSemanticModel(declaration.SyntaxTree);
                var bodyNodes = (declaration.Body ?? (SyntaxNode)declaration.ExpressionBody!).DescendantNodes().ToList();
                foreach (var identifier in bodyNodes.OfType<IdentifierNameSyntax>())
                {
                    if (model.GetSymbolInfo(identifier).Symbol is ITypeParameterSymbol tp)
                    {
                        AddVisibleRef(tp, visible, referenced);
                    }
                }

                foreach (var invocation in bodyNodes.OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee
                        || !planned.ContainsKey(callee.OriginalDefinition))
                    {
                        continue;
                    }

                    foreach (var argument in callee.TypeArguments)
                    {
                        if (argument is ITypeParameterSymbol tp)
                        {
                            AddVisibleRef(tp, visible, referenced);
                        }
                    }
                }

                if (referenced.Count == 0) continue;
                var set = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
                foreach (var reference in referenced)
                {
                    set.Add(reference);
                }

                planned[method] = set;
                changed = true;
            }
        }
        while (changed);

        foreach (var (method, declaration, _) in pool)
        {
            if (!planned.TryGetValue(method, out var referenced)) continue;

            var callSites = CollectStaticMethodCallSites(method, declaration, referenced);
            if (callSites == null)
            {
                continue;
            }

            _staticMethods.Add(new StaticMethodPlan
            {
                Method = method,
                Declaration = declaration,
                Params = referenced.ToList(),
                ConstraintTexts = CollectConstraintTexts(declaration, referenced),
                CallSites = callSites,
            });
        }

        _staticMethods.Sort((a, b) => string.CompareOrdinal(a.FilePath, b.FilePath) != 0
            ? string.CompareOrdinal(a.FilePath, b.FilePath)
            : a.Declaration.SpanStart.CompareTo(b.Declaration.SpanStart));
    }

    private static void AddVisibleRef(
        ITypeParameterSymbol tp,
        List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> visible,
        List<GenericDeepenParamRef> referenced)
    {
        var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, tp));
        if (match.Ref != null && !referenced.Contains(match.Ref))
        {
            referenced.Add(match.Ref);
        }
    }

    /// <summary>Static properties referencing class parameters are not rewritable in v1 — surface as CS2J1102.</summary>
    private void ReportStaticPropertyGaps()
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var property in tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (!property.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) continue;
                if (model.GetDeclaredSymbol(property) is not IPropertySymbol symbol) continue;

                var visible = CollectVisibleClassParams(symbol);
                if (visible.Count == 0) continue;

                var referenced = new List<GenericDeepenParamRef>();
                CollectTypeReferences(symbol.Type, visible, referenced);
                if (referenced.Count == 0) continue;

                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.StaticMember, GenericDeepenCodes.StaticFamilyNotClosable,
                    $"Static property '{symbol.ToDisplayString()}' references enclosing type parameters; static properties are not rewritable in v1 and are left in place.",
                    tree.FilePath, property.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
        }
    }

    /// <summary>Any reference to the method outside an invocation operand (delegate/method-group conversion).</summary>
    private bool HasMethodGroupReference(IMethodSymbol method)
    {
        if (!_identifiersBySymbol.TryGetValue(method, out var references))
        {
            return false;
        }

        foreach (var site in references)
        {
            var identifier = site.Identifier;
            if (identifier.Identifier.ValueText != method.Name) continue;
            if (IsWithinNameOf(identifier)) continue;
            if (IsInvocationOperand(identifier)) continue;
            return true;
        }

        return false;
    }

    private static bool IsInvocationOperand(IdentifierNameSyntax identifier)
        => identifier.Parent switch
        {
            InvocationExpressionSyntax invocation when invocation.Expression == identifier => true,
            MemberAccessExpressionSyntax member when member.Name == identifier
                => member.Parent is InvocationExpressionSyntax invocation && invocation.Expression == member,
            _ => false,
        };

    /// <summary>Renders the call-site type arguments (receiver construction or enclosing declaration), like field usages.</summary>
    private IReadOnlyList<string>? RenderMethodTypeArguments(
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol containingType,
        IEnumerable<GenericDeepenParamRef> referenced)
    {
        // Nested non-generic classes (Outer<T>.Inner) find their construction through
        // any generic ancestor type name in the containing chain.
        var chainNames = new HashSet<string>(StringComparer.Ordinal);
        for (var type = containingType.OriginalDefinition; type != null; type = type.ContainingType?.OriginalDefinition)
        {
            chainNames.Add(type.Name);
        }

        var texts = new List<string>();

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var generic = memberAccess.Expression.DescendantNodesAndSelf()
                .OfType<GenericNameSyntax>()
                .FirstOrDefault(g => chainNames.Contains(g.Identifier.ValueText));
            if (generic == null)
            {
                return null;
            }

            foreach (var param in referenced)
            {
                texts.Add(generic.TypeArgumentList.Arguments[param.Ordinal].ToString());
            }

            return texts;
        }

        if (invocation.Expression is IdentifierNameSyntax)
        {
            foreach (var ancestor in invocation.Ancestors().OfType<TypeDeclarationSyntax>())
            {
                if (ancestor.TypeParameterList == null || !chainNames.Contains(ancestor.Identifier.ValueText))
                {
                    continue;
                }

                foreach (var param in referenced)
                {
                    texts.Add(ancestor.TypeParameterList.Parameters[param.Ordinal].Identifier.ValueText);
                }

                return texts;
            }
        }

        return null;
    }

    /// <summary>All invocations of the method; null means a call site could not be resolved (CS2J1106, method left in place).</summary>
    private List<StaticMethodCallSitePlan>? CollectStaticMethodCallSites(
        IMethodSymbol method, MethodDeclarationSyntax declaration, SortedSet<GenericDeepenParamRef> referenced)
    {
        var sites = new List<StaticMethodCallSitePlan>();
        if (!_invocationsByMethod.TryGetValue(method, out var matchingInvocations))
        {
            return sites;
        }

        foreach (var site in matchingInvocations)
        {
            var invocation = site.Invocation;
            var args = RenderMethodTypeArguments(invocation, method.ContainingType, referenced);
            if (args == null)
            {
                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.StaticMember, GenericDeepenCodes.CallSiteOutsideCompilation,
                    $"Call site '{invocation}' of hoisted static method '{method.ToDisplayString()}' does not reveal the closed type construction; the method is left in place.",
                    invocation.SyntaxTree.FilePath, invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                return null;
            }

            sites.Add(new StaticMethodCallSitePlan
            {
                Invocation = invocation,
                TypeArgumentTexts = args,
            });
        }

        return sites;
    }

    // ---- phase 1: per-tree walk ----

    private void WalkTree(SyntaxTree tree)
    {
        var model = _compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        foreach (var md in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(md);
            if (symbol == null) continue;
            var original = symbol.OriginalDefinition;
            _methodDecls[original] = md;
            foreach (var need in CollectDirectNeeds(symbol, md, model))
            {
                AddNeed(original, need);
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            CollectForwardEdge(invocation, model);
        }
    }

    private IEnumerable<GenericDeepenParamRef> CollectDirectNeeds(
        IMethodSymbol method, MethodDeclarationSyntax declaration, SemanticModel model)
    {
        var needs = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
        var visible = CollectVisibleParams(method);
        if (declaration.Body == null && declaration.ExpressionBody == null)
        {
            return needs;
        }

        var bodyNodes = (declaration.Body ?? (SyntaxNode)declaration.ExpressionBody!).DescendantNodes().ToList();
        foreach (var node in bodyNodes)
        {
            switch (node)
            {
                case TypeOfExpressionSyntax typeOf:
                    if (ResolveTypeParameter(model, typeOf.Type, visible, declaration) is { } typeofRef)
                    {
                        RegisterTokenSite(method, declaration, model, typeOf, typeofRef.Symbol, typeofRef.Ref, needs);
                    }

                    break;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression)
                                                       || binary.IsKind(SyntaxKind.AsExpression):
                    if (binary.Right is TypeSyntax rightType
                        && ResolveTypeParameter(model, rightType, visible, declaration) is { } binaryRef)
                    {
                        RegisterTokenSite(method, declaration, model, binary, binaryRef.Symbol, binaryRef.Ref, needs);
                    }

                    break;
                case IsPatternExpressionSyntax pattern:
                    CollectPattern(method, declaration, pattern, model, visible, needs);
                    break;
                case CatchClauseSyntax catchClause:
                    CollectCatch(method, declaration, catchClause, model, visible, needs);
                    break;
            }
        }

        return needs;
    }

    /// <summary>
    /// Resolves a type syntax used at a token site to its type parameter. Besides the
    /// method/class type parameters in <paramref name="visible"/>, accepts a type
    /// parameter owned by a local function declared inside <paramref name="declaration"/>
    /// (GD-NEST: such a parameter is anchored at its own local-function layer only).
    /// </summary>
    private (ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)? ResolveTypeParameter(
        SemanticModel model,
        TypeSyntax type,
        List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> visible,
        MethodDeclarationSyntax declaration)
    {
        var syntax = type;
        if (syntax is NullableTypeSyntax nullable) syntax = nullable.ElementType;
        if (syntax is ArrayTypeSyntax array) syntax = array.ElementType;
        if (syntax is not IdentifierNameSyntax identifier) return null;
        if (model.GetSymbolInfo(identifier).Symbol is not ITypeParameterSymbol tp) return null;

        var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, tp));
        if (match.Ref != null)
        {
            return (tp, match.Ref);
        }

        var ownerClosure = DeclaringClosure(tp);
        if (ownerClosure != null && declaration.Span.Contains(ownerClosure.SpanStart))
        {
            return (tp, new GenericDeepenParamRef(GenericDeepenParamKind.MethodTypeParameter, tp.Ordinal, tp.Name));
        }

        return null;
    }

    /// <summary>
    /// Registers a token-need <paramref name="paramRef"/> (symbol <paramref name="tp"/>)
    /// used at <paramref name="site"/>: the declaring scope gets its method/closure
    /// anchor and every closure layer between the site and that scope gets one too
    /// (GD-NEST). Sites inside quoted expression-tree lambdas belong to GD-EXPRTREE
    /// (no rewrite inside the tree, method-level anchors stay outside it).
    /// </summary>
    private void RegisterTokenSite(
        IMethodSymbol method,
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        SyntaxNode site,
        ITypeParameterSymbol tp,
        GenericDeepenParamRef paramRef,
        SortedSet<GenericDeepenParamRef> methodNeeds)
    {
        if (CSharpToJava.Core.Analysis.ExpressionTreeFacts.IsInsideQuotedExpressionLambda(model, site))
        {
            ReportExpressionTreeSite(model, site);
            return;
        }

        var ownerClosure = DeclaringClosure(tp);

        // Class/method-declared parameters anchor the method itself; a parameter
        // declared by a local function is not even in scope at method level.
        if (ownerClosure == null)
        {
            methodNeeds.Add(paramRef);
        }

        if (!_options.PropagateNestedTokens)
        {
            return;
        }

        foreach (var closure in ClosureChain(site, declaration, ownerClosure))
        {
            if (!IsClosureAnchorInsertable(closure, model))
            {
                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.Nested, GenericDeepenCodes.UndecidableSite,
                    $"Token anchor for type parameter '{paramRef.Name}' cannot be placed in the enclosing " +
                    $"closure (expression-bodied lambda without a resolvable delegate shape, or dynamic/unbound conversion); " +
                    $"the closure at {closure.GetLocation().GetLineSpan().StartLinePosition.Line + 1} is left without an anchor.",
                    declaration.SyntaxTree.FilePath,
                    site.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }

            if (!_closureNeeds.TryGetValue(closure, out var set))
            {
                set = new SortedSet<GenericDeepenParamRef>(NamedParamRefComparer.Instance);
                _closureNeeds[closure] = set;
            }

            if (set.Add(paramRef))
            {
                // Fires only once per (closure layer, parameter) across every site.
                _analysisStats.NestedPropagations++;
            }
        }
    }

    private readonly GenericDeepenStatistics _analysisStats = new();

    /// <summary>
    /// Closure syntax layers (innermost first) between <paramref name="site"/> and
    /// <paramref name="declaration"/>, stopping AT the local function that declares
    /// the parameter (layers above it cannot name it).
    /// </summary>
    private static List<SyntaxNode> ClosureChain(
        SyntaxNode site,
        MethodDeclarationSyntax declaration,
        LocalFunctionStatementSyntax? ownerClosure)
    {
        var chain = new List<SyntaxNode>();
        foreach (var ancestor in site.Ancestors())
        {
            if (ancestor == declaration) break;
            if (ancestor is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax))
            {
                continue;
            }

            chain.Add(ancestor);
            if (ownerClosure is not null && ancestor == ownerClosure) break;
        }

        return chain;
    }

    /// <summary>The local function declaring <paramref name="tp"/>, or null when the owner is the method/type.</summary>
    private LocalFunctionStatementSyntax? DeclaringClosure(ITypeParameterSymbol tp)
    {
        if (_closureOwners.TryGetValue(tp, out var cached))
        {
            return cached;
        }

        LocalFunctionStatementSyntax? owner = null;
        if (tp.DeclaringSyntaxReferences is { Length: > 0 } references
            && references[0].GetSyntax() is { } syntax
            && syntax.Parent?.Parent is LocalFunctionStatementSyntax localFunction)
        {
            owner = localFunction;
        }

        _closureOwners[tp] = owner;
        return owner;
    }

    /// <summary>
    /// A closure can host anchor statements when it already has a block body, or
    /// when its expression body can deterministically be wrapped (local function
    /// with a spelled return type, or a lambda bound to a known delegate).
    /// </summary>
    private static bool IsClosureAnchorInsertable(SyntaxNode closure, SemanticModel model)
    {
        switch (closure)
        {
            case AnonymousMethodExpressionSyntax:
                return true;
            case LocalFunctionStatementSyntax { Body: not null }:
                return true;
            case LocalFunctionStatementSyntax localFunction:
                return localFunction.ReturnType is PredefinedTypeSyntax predefined
                    && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword)
                    || localFunction.ReturnType is not null;
            case LambdaExpressionSyntax { Block: not null }:
                return true;
            case LambdaExpressionSyntax lambda:
                // Expression-bodied lambda: wrapping needs the delegate's return-ness.
                // A dynamic/unbound conversion gives no INamedTypeSymbol here.
                if (model.GetTypeInfo(lambda).ConvertedType is not INamedTypeSymbol converted)
                {
                    return false;
                }

                return converted.DelegateInvokeMethod != null;
            default:
                return false;
        }
    }

    private void ReportExpressionTreeSite(SemanticModel model, SyntaxNode site)
    {
        var severity = _options.ExpressionTreePolicy == GenericDeepenExpressionTreePolicy.Error
            ? GenericDeepenSeverity.Warning
            : GenericDeepenSeverity.Info;
        _diagnostics.Add(new GenericDeepenDiagnostic(
            severity, GenericDeepenRule.Nested, GenericDeepenCodes.ExpressionTreeSite,
            $"Generic token site '{site.ToString().Trim()}' is inside a quoted expression-tree lambda; " +
            "the tree node is not rewritten and no anchor is inserted inside the lambda (GD-EXPRTREE).",
            site.SyntaxTree?.FilePath,
            site.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        _analysisStats.ExpressionTreeSitesSkipped++;
    }

    private void CollectPattern(
        IMethodSymbol method,
        MethodDeclarationSyntax declaration,
        IsPatternExpressionSyntax pattern,
        SemanticModel model,
        List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> visible,
        SortedSet<GenericDeepenParamRef> needs)
    {
        if (!_options.RewriteIsAsPatterns
            || pattern.Pattern is not DeclarationPatternSyntax declarationPattern
            || ResolveTypeParameter(model, declarationPattern.Type, visible, declaration) is not { } resolved)
        {
            return;
        }

        var tp = resolved.Symbol;

        string variableName;
        bool isDiscard;
        switch (declarationPattern.Designation)
        {
            case SingleVariableDesignationSyntax designation:
                variableName = designation.Identifier.ValueText;
                isDiscard = designation.Identifier.IsKind(SyntaxKind.UnderscoreToken);
                break;
            case DiscardDesignationSyntax:
                variableName = "_";
                isDiscard = true;
                break;
            default:
                return;
        }

        // Token need so the declaring scope and every closure layer get anchors.
        RegisterTokenSite(method, declaration, model, pattern, tp, resolved.Ref, needs);

        IfStatementSyntax? conditionOwner = pattern.Parent as IfStatementSyntax;
        var liftable = conditionOwner != null
        && conditionOwner.Condition == pattern
        && conditionOwner.Statement is BlockSyntax;

        var usedAfter = false;
        if (liftable && conditionOwner!.Parent is BlockSyntax enclosing)
        {
            var after = enclosing.Statements.Where(s => s.SpanStart >= conditionOwner.Span.End);
            usedAfter = !isDiscard && after.Any(s => s.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(i => i.Identifier.ValueText == variableName));
        }

        if (!liftable || usedAfter)
        {
            _diagnostics.Add(new GenericDeepenDiagnostic(
                GenericDeepenSeverity.Warning, GenericDeepenRule.IsAs, GenericDeepenCodes.PatternNotLiftable,
                $"Type-parameter pattern '{pattern}' is not statement-liftable here (composite condition, non-block body, or the pattern variable is used after the if); rewrite manually.",
                declaration.SyntaxTree.FilePath,
                pattern.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            return;
        }

        _patterns.Add(new TypeParamPatternPlan
        {
            Pattern = pattern,
            If = conditionOwner!,
            Param = resolved.Ref,
            VariableName = variableName,
            TempName = $"__cs2j_pat{_patternTempCounter++}",
            IsDiscard = isDiscard,
        });
    }

    private void CollectCatch(
        IMethodSymbol method,
        MethodDeclarationSyntax declaration,
        CatchClauseSyntax clause,
        SemanticModel model,
        List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> visible,
        SortedSet<GenericDeepenParamRef> needs)
    {
        if (!_options.RewriteCatchTypeParam
            || clause.Declaration?.Type is not { } catchType
            || ResolveTypeParameter(model, catchType, visible, declaration) is not { } catchResolved)
        {
            return;
        }

        RegisterTokenSite(method, declaration, model, clause, catchResolved.Symbol, catchResolved.Ref, needs);

        if (clause.Filter != null)
        {
            _diagnostics.Add(new GenericDeepenDiagnostic(
                GenericDeepenSeverity.Warning, GenericDeepenRule.Catch, GenericDeepenCodes.CatchLoweringPreconditionFailed,
                $"catch over type parameter '{catchType.ToString()}' with a filter cannot be lowered automatically; rewrite manually.",
                declaration.SyntaxTree.FilePath,
                clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            return;
        }

        _catches.Add(new CatchTypeParamPlan
        {
            Clause = clause,
            Param = catchResolved.Ref,
            VariableName = string.IsNullOrEmpty(clause.Declaration?.Identifier.ValueText)
                ? null
                : clause.Declaration.Identifier.ValueText,
            ExceptionVariableName = $"__cs2j_e{_catches.Count}",
        });
    }

    private static List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> CollectVisibleParams(IMethodSymbol method)
    {
        var result = new List<(ITypeParameterSymbol, GenericDeepenParamRef)>();
        if (method.IsGenericMethod)
        {
            foreach (var tp in method.OriginalDefinition.TypeParameters)
            {
                result.Add((tp, new GenericDeepenParamRef(GenericDeepenParamKind.MethodTypeParameter, tp.Ordinal, tp.Name)));
            }
        }

        var type = method.ContainingType?.OriginalDefinition;
        while (type != null)
        {
            foreach (var tp in type.TypeParameters)
            {
                result.Add((tp, new GenericDeepenParamRef(GenericDeepenParamKind.ClassTypeParameter, tp.Ordinal, tp.Name)));
            }

            type = type.ContainingType?.OriginalDefinition;
        }

        return result;
    }

    private void CollectForwardEdge(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (!_options.PropagateNestedTokens)
        {
            return;
        }

        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee || !callee.IsGenericMethod)
        {
            return;
        }

        // Inside a lambda/local function GetEnclosingSymbol returns the anonymous
        // function/local-function symbol; the forwarding edge belongs to the method
        // that owns them (the chain closures get their own GD-NEST anchors).
        var caller = ResolveEnclosingSourceMethod(invocation, model);
        if (caller == null || !_methodDecls.ContainsKey(caller.OriginalDefinition)) return;

        var visible = CollectVisibleParams(caller);
        for (var i = 0; i < callee.TypeArguments.Length; i++)
        {
            if (callee.TypeArguments[i] is not ITypeParameterSymbol tp) continue;
            var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, tp));
            if (match.Ref == null) continue;
            if (!_forwardEdges.TryGetValue(caller.OriginalDefinition, out var edges))
            {
                edges = new List<ForwardEdge>();
                _forwardEdges[caller.OriginalDefinition] = edges;
            }

            edges.Add(new ForwardEdge(callee.OriginalDefinition, i, match.Ref, invocation));
        }
    }

    /// <summary>The ordinary source method enclosing <paramref name="node"/> (crossing lambda/local-function boundaries).</summary>
    private IMethodSymbol? ResolveEnclosingSourceMethod(SyntaxNode node, SemanticModel model)
    {
        var declaration = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return declaration == null ? null : model.GetDeclaredSymbol(declaration);
    }

    // ---- phase 2: families (override chains + interface implementations) ----

    private void BuildFamilies()
    {
        if (!_options.PropagateNestedTokens)
        {
            return;
        }

        foreach (var type in AllTypes(_compilation.Assembly.GlobalNamespace))
        {
            foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind == MethodKind.Ordinary && member.IsOverride && member.OverriddenMethod != null)
                {
                    var root = member;
                    while (root.OverriddenMethod != null) root = root.OverriddenMethod;
                    AddToFamily(root.OriginalDefinition, root.OriginalDefinition);
                    AddToFamily(root.OriginalDefinition, member.OriginalDefinition);
                }
            }

            foreach (var iface in type.AllInterfaces)
            {
                foreach (var ifaceMethod in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (type.FindImplementationForInterfaceMember(ifaceMethod) is IMethodSymbol impl)
                    {
                        AddToFamily(ifaceMethod.OriginalDefinition, ifaceMethod.OriginalDefinition);
                        AddToFamily(ifaceMethod.OriginalDefinition, impl.OriginalDefinition);
                    }
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        foreach (var nested in root.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in AllTypes(nested))
            {
                yield return deeper;
            }
        }

        foreach (var ns in root.GetNamespaceMembers())
        {
            foreach (var type in AllTypes(ns))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in AllTypes(nested))
            {
                yield return deeper;
            }
        }
    }

    private void AddToFamily(IMethodSymbol root, IMethodSymbol member)
    {
        if (!_families.TryGetValue(root, out var members))
        {
            members = new List<IMethodSymbol>();
            _families[root] = members;
        }

        if (!members.Contains(member, SymbolEqualityComparer.Default))
        {
            members.Add(member);
        }
    }

    // ---- phase 3: fixpoint (family union + forwarding propagation) ----

    private void PropagateNeeds()
    {
        if (!_options.PropagateNestedTokens)
        {
            return;
        }

        bool changed;
        do
        {
            changed = false;

            foreach (var (root, members) in _families)
            {
                var union = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
                foreach (var member in members)
                {
                    if (_needs.TryGetValue(member, out var memberNeeds))
                    {
                        foreach (var need in memberNeeds)
                        {
                            union.Add(need);
                        }
                    }
                }

                foreach (var member in members)
                {
                    var arity = member.OriginalDefinition.TypeParameters.Length;
                    if (!_needs.TryGetValue(member, out var current))
                    {
                        current = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
                        _needs[member] = current;
                    }

                    foreach (var need in union)
                    {
                        GenericDeepenParamRef? mapped;
                        if (need.Kind == GenericDeepenParamKind.MethodTypeParameter)
                        {
                            if (need.Ordinal >= arity)
                            {
                                var gapKey = $"{root.ToDisplayString()}|{need.Kind}#{need.Ordinal}";
                                if (_reportedFamilyGaps.Add(gapKey))
                                {
                                    _diagnostics.Add(new GenericDeepenDiagnostic(
                                        GenericDeepenSeverity.Warning, GenericDeepenRule.Anchor,
                                        GenericDeepenCodes.StaticFamilyNotClosable,
                                        $"Override/interface family of '{root.ToDisplayString()}' needs a token for method type parameter #{need.Ordinal} "
                                        + "but member '" + member.ToDisplayString() + "' has fewer type parameters; family sync skipped for that parameter.",
                                        member.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                                        member.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1));
                                }

                                continue;
                            }

                            // Re-name in case siblings declared the ordinal differently.
                            mapped = new GenericDeepenParamRef(
                                GenericDeepenParamKind.MethodTypeParameter,
                                need.Ordinal,
                                member.TypeParameters[need.Ordinal].Name);
                        }
                        else
                        {
                            // Class type parameter: only siblings whose base/interface
                            // construction keeps it open carry an anchor; a closed
                            // sibling (UriConverter : JsonConverter<Uri>) needs nothing.
                            mapped = MapClassNeedToMember(root, member, need.Ordinal);
                            if (mapped == null)
                            {
                                continue;
                            }
                        }

                        if (current.Add(mapped!))
                        {
                            changed = true;
                        }
                    }
                }
            }

            foreach (var (caller, edges) in _forwardEdges)
            {
                foreach (var edge in edges)
                {
                    if (!_needs.TryGetValue(edge.Callee, out var calleeNeeds)) continue;
                    if (!HasKindOrdinal(calleeNeeds, GenericDeepenParamKind.MethodTypeParameter, edge.CalleeParamOrdinal))
                    {
                        continue;
                    }

                    if (!_needs.TryGetValue(caller, out var callerNeeds))
                    {
                        callerNeeds = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
                        _needs[caller] = callerNeeds;
                    }

                    if (callerNeeds.Add(edge.CallerParam))
                    {
                        changed = true;
                    }

                    // GD-NEST: a forwarding call inside a lambda/local function also
                    // anchors every closure layer between the call and the method.
                    PropagateEdgeIntoClosures(caller, edge);
                }
            }
        }
        while (changed);
    }

    private void PropagateEdgeIntoClosures(IMethodSymbol caller, ForwardEdge edge)
    {
        var invocation = edge.Invocation;
        if (!_methodDecls.TryGetValue(caller, out var declaration))
        {
            return;
        }

        var model = _compilation.GetSemanticModel(invocation.SyntaxTree);
        if (CSharpToJava.Core.Analysis.ExpressionTreeFacts.IsInsideQuotedExpressionLambda(model, invocation))
        {
            // The call node belongs to a quoted tree; only the method-level anchor
            // (added above, outside the lambda) may serve it — trace and move on.
            if (_reportedTreeEdges.Add(invocation.SpanStart))
            {
                ReportExpressionTreeSite(model, invocation);
            }

            return;
        }

        foreach (var closure in ClosureChain(invocation, declaration, null))
        {
            if (!IsClosureAnchorInsertable(closure, model))
            {
                _diagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.Nested, GenericDeepenCodes.UndecidableSite,
                    $"Forwarded token for type parameter '{edge.CallerParam.Name}' cannot place an anchor in the enclosing " +
                    "closure (expression-bodied lambda without a resolvable delegate shape, or dynamic/unbound conversion).",
                    invocation.SyntaxTree.FilePath,
                    invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
                continue;
            }

            if (!_closureNeeds.TryGetValue(closure, out var set))
            {
                set = new SortedSet<GenericDeepenParamRef>(NamedParamRefComparer.Instance);
                _closureNeeds[closure] = set;
            }

            if (set.Add(edge.CallerParam))
            {
                _analysisStats.NestedPropagations++;
            }
        }
    }

    private readonly HashSet<int> _reportedTreeEdges = new();

    private static bool HasKindOrdinal(IEnumerable<GenericDeepenParamRef> needs, GenericDeepenParamKind kind, int ordinal)
        => needs.Any(n => n.Kind == kind && n.Ordinal == ordinal);

    /// <summary>
    /// Maps a class type-parameter need of the family root's declaring type onto
    /// the frame of <paramref name="member"/>'s declaring type. Returns null when
    /// the sibling closes that parameter (e.g. <c>UriConverter : JsonConverter&lt;Uri&gt;</c>)
    /// — such a sibling carries no open token and must not be anchored.
    /// </summary>
    private static GenericDeepenParamRef? MapClassNeedToMember(IMethodSymbol root, IMethodSymbol member, int rootOrdinal)
    {
        var rootType = root.ContainingType?.OriginalDefinition;
        var memberType = member.ContainingType?.OriginalDefinition;
        if (rootType == null || memberType == null)
        {
            return null;
        }

        ITypeSymbol? substituted = null;
        if (SymbolEqualityComparer.Default.Equals(memberType, rootType))
        {
            substituted = memberType.TypeParameters.Length > rootOrdinal
                ? memberType.TypeParameters[rootOrdinal]
                : null;
        }
        else
        {
            for (var baseType = memberType.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, rootType))
                {
                    substituted = rootOrdinal < baseType.TypeArguments.Length
                        ? baseType.TypeArguments[rootOrdinal]
                        : null;
                    break;
                }
            }

            if (substituted == null && rootType.TypeKind == TypeKind.Interface)
            {
                foreach (var iface in memberType.AllInterfaces)
                {
                    if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, rootType)
                        && rootOrdinal < iface.TypeArguments.Length)
                    {
                        substituted = iface.TypeArguments[rootOrdinal];
                        break;
                    }
                }
            }
        }

        if (substituted is not ITypeParameterSymbol siblingTp)
        {
            // Concrete closure (Uri, int, …) or an otherwise non-parameter argument.
            return null;
        }

        // Only parameters declared by the sibling's own type chain are anchorable
        // at this member; method/enclosing-method parameters never appear here.
        var owner = siblingTp.DeclaringType?.OriginalDefinition;
        for (var t = memberType; t != null; t = t.ContainingType?.OriginalDefinition)
        {
            if (SymbolEqualityComparer.Default.Equals(t, owner))
            {
                return new GenericDeepenParamRef(
                    GenericDeepenParamKind.ClassTypeParameter, siblingTp.Ordinal, siblingTp.Name);
            }
        }

        return null;
    }

    private void AddNeed(IMethodSymbol method, GenericDeepenParamRef need)
    {
        if (!_needs.TryGetValue(method, out var set))
        {
            set = new SortedSet<GenericDeepenParamRef>(ParamRefComparer.Instance);
            _needs[method] = set;
        }

        set.Add(need);
    }
}
