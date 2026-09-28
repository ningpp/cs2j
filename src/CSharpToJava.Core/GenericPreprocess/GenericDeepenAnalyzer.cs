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

/// <summary>Whole-compilation analysis result: plans keyed by symbol, sorted by (file, span).</summary>
public sealed class GenericDeepenAnalysis
{
    public required IReadOnlyList<TokenAnchorPlan> Anchors { get; init; }
    public required IReadOnlyList<GenericDeepenDiagnostic> Diagnostics { get; init; }
    public required GenericDeepenStatistics Statistics { get; init; }

    public static GenericDeepenAnalysis Empty { get; } = new()
    {
        Anchors = Array.Empty<TokenAnchorPlan>(),
        Diagnostics = Array.Empty<GenericDeepenDiagnostic>(),
        Statistics = new GenericDeepenStatistics(),
    };
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

    private readonly record struct ForwardEdge(IMethodSymbol Callee, int CalleeParamOrdinal, GenericDeepenParamRef CallerParam);

    private sealed class ParamRefComparer : IComparer<GenericDeepenParamRef>
    {
        public static readonly ParamRefComparer Instance = new();

        public int Compare(GenericDeepenParamRef? x, GenericDeepenParamRef? y)
            => (x ?? throw new ArgumentNullException(nameof(x))).CompareTo(y);
    }

    public GenericDeepenAnalyzer(Compilation compilation, GenericDeepenOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    public GenericDeepenAnalysis Analyze()
    {
        if (!_options.RewriteTypeOfAnchors)
        {
            return GenericDeepenAnalysis.Empty;
        }

        foreach (var tree in _compilation.SyntaxTrees)
        {
            WalkTree(tree);
        }

        BuildFamilies();
        PropagateNeeds();

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

        return new GenericDeepenAnalysis
        {
            Anchors = anchors,
            Diagnostics = _diagnostics,
            Statistics = new GenericDeepenStatistics(),
        };
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
        if (visible.Count == 0 || declaration.Body == null && declaration.ExpressionBody == null)
        {
            return needs;
        }

        var bodyNodes = (declaration.Body ?? (SyntaxNode)declaration.ExpressionBody!).DescendantNodes().ToList();
        foreach (var node in bodyNodes)
        {
            switch (node)
            {
                case TypeOfExpressionSyntax typeOf:
                    AddIfVisible(model, typeOf.Type, visible, needs);
                    break;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression)
                                                       || binary.IsKind(SyntaxKind.AsExpression):
                    if (binary.Right is TypeSyntax rightType)
                    {
                        AddIfVisible(model, rightType, visible, needs);
                    }

                    break;
            }
        }

        return needs;
    }

    private static void AddIfVisible(
        SemanticModel model, TypeSyntax type,
        List<(ITypeParameterSymbol Symbol, GenericDeepenParamRef Ref)> visible,
        ISet<GenericDeepenParamRef> needs)
    {
        var syntax = type;
        if (syntax is NullableTypeSyntax nullable) syntax = nullable.ElementType;
        if (syntax is ArrayTypeSyntax array) syntax = array.ElementType;
        if (syntax is not IdentifierNameSyntax identifier) return;
        var symbol = model.GetSymbolInfo(identifier).Symbol as ITypeParameterSymbol;
        if (symbol == null) return;
        var match = visible.FirstOrDefault(v => SymbolEqualityComparer.Default.Equals(v.Symbol, symbol));
        if (match.Ref != null)
        {
            needs.Add(match.Ref);
        }
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

        var caller = model.GetEnclosingSymbol(invocation.SpanStart) as IMethodSymbol;
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

            edges.Add(new ForwardEdge(callee.OriginalDefinition, i, match.Ref));
        }
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
                        if (need.Kind == GenericDeepenParamKind.MethodTypeParameter && need.Ordinal >= arity)
                        {
                            var gapKey = $"{root.ToDisplayString()}|{need.Kind}#{need.Ordinal}";
                            if (_reportedFamilyGaps.Add(gapKey))
                            {
                                _diagnostics.Add(new GenericDeepenDiagnostic(
                                    GenericDeepenSeverity.Warning, GenericDeepenRule.Anchor,
                                    GenericDeepenCodes.StaticFamilyNotClosable,
                                    $"Override/interface family of '{root.ToDisplayString()}' needs a token for method type parameter #{need.Ordinal} " +
                                    "but member '" + member.ToDisplayString() + "' has fewer type parameters; family sync skipped for that parameter.",
                                    member.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                                    member.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1));
                            }

                            continue;
                        }

                        if (current.Add(need))
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
                }
            }
        }
        while (changed);
    }

    private static bool HasKindOrdinal(IEnumerable<GenericDeepenParamRef> needs, GenericDeepenParamKind kind, int ordinal)
        => needs.Any(n => n.Kind == kind && n.Ordinal == ordinal);

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
