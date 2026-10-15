using System.Collections.Concurrent;
using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.VarianceAudit;

/// <summary>One participating variant type-parameter entry of a use site (design §4.3/D7).</summary>
public sealed record VarianceTypeArgumentUse(
    string ParameterName,
    VarianceKind DeclarationKind,
    UseFlow Flow,
    string ArgumentType,
    string SuggestedWildcard);

/// <summary>A variant type-parameter declaration site (out/in on a source interface/delegate).</summary>
public sealed record VariantDeclarationRecord(
    VarianceContainer Container,
    string TypeName,
    string TypeParameterName,
    VarianceKind Kind,
    IReadOnlyList<string> Constraints,
    VarianceOrigin Origin,
    bool BaselineHasErrors,
    string? FilePath = null,
    int Line = 0,
    int Column = 0);

/// <summary>
/// A use site of a variant closed type, a bare variant type parameter, or a covariant
/// reference-type array, with its position-direction flow and (D7) composition chain.
/// </summary>
public sealed record VarianceUseRecord(
    UseFlow Flow,
    string TypeDisplayName,
    string SuggestedWildcard,
    IReadOnlyList<VarianceTypeArgumentUse> TypeArguments,
    IReadOnlyList<string> CompositionChain,
    UseTag Tags,
    bool EnclosedByExpressionTree,
    bool EnclosedByDynamic,
    bool VarianceConversion,
    VarianceOrigin Origin,
    bool BaselineHasErrors,
    VarianceClassification Classification = VarianceClassification.Safe,
    string? MappedJavaType = null,
    IReadOnlyList<string>? MutatingMembers = null,
    bool MemberResolutionIncomplete = false,
    string? FilePath = null,
    int Line = 0,
    int Column = 0);

/// <summary>
/// Whole-tree enumeration of variance declaration and use sites (design §4.3, batch 2/M1b).
/// Declaration sites come from the semantic model (D4); use sites cover the positions listed
/// in §4.3 with D3① position-direction and D7 nested composition. Safety classification and
/// variance-conversion detection belong to batch 3.
/// </summary>
/// <remarks>
/// Performance: declaration symbols are walked once (cheap), while use-site collection is
/// embarrassingly per-tree and runs in parallel over the compilation's syntax trees. All
/// semantic queries for one tree are scoped to a <see cref="UseCollector"/> (ancestor
/// caches live there), and sites are deterministically re-sorted after the merge.
/// </remarks>
internal sealed class VarianceSiteInventory
{
    private const string WildcardInvariant = "invariant";

    private readonly Compilation _compilation;
    private readonly VarianceAuditStatistics _statistics;
    private readonly VarianceBaselineCache _baseline;
    private readonly HashSet<string> _metadataVariantTypes = new(StringComparer.Ordinal);
    private readonly List<VariantDeclarationRecord> _declarations = new();
    private readonly List<VarianceUseRecord> _uses = new();

    public VarianceSiteInventory(
        Compilation compilation,
        VarianceAuditStatistics statistics,
        VarianceBaselineCache baseline)
    {
        _compilation = compilation;
        _statistics = statistics;
        _baseline = baseline;
    }

    public VarianceAuditAnalysis Build()
    {
        CollectDeclarations(_compilation.Assembly.GlobalNamespace);

        // Syntax pre-screen shared by all trees: bind a type position only when its
        // shape could possibly be a variant use (reference array, a known/source
        // variant generic, or a bare type parameter).
        var hintNames = BuildHintNames();

        var perTreeResults = new ConcurrentBag<UseCollector>();
        Parallel.ForEach(
            _compilation.SyntaxTrees,
            () => new List<UseCollector>(),
            (tree, _, local) =>
            {
                local.Add(UseCollector.Collect(_compilation, tree, _baseline, hintNames));
                return local;
            },
            local =>
            {
                foreach (var collector in local)
                {
                    perTreeResults.Add(collector);
                }
            });

        foreach (var result in perTreeResults)
        {
            _uses.AddRange(result.Uses);
            _metadataVariantTypes.UnionWith(result.MetadataVariantTypes);
        }

        SortSites();
        FillStatistics();

        var analysis = new VarianceAuditAnalysis();
        analysis.DeclarationSites.AddRange(_declarations);
        analysis.UseSites.AddRange(_uses);
        return analysis;
    }

    /// <summary>
    /// Short type names worth binding at a use position: the well-known variant BCL
    /// types plus every source-declared variant interface/delegate found in this run.
    /// </summary>
    private HashSet<string> BuildHintNames()
    {
        var names = new HashSet<string>(
            UseSiteSafetyClassifier.KnownVariantTypeNames, StringComparer.Ordinal);
        foreach (var declaration in _declarations)
        {
            var name = declaration.TypeName;
            var dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                name = name[(dot + 1)..];
            }

            var tick = name.IndexOfAny(new[] { '`', '<' });
            if (tick >= 0)
            {
                name = name[..tick];
            }

            names.Add(name);
        }

        return names;
    }

    // ---- declaration sites (D4: semantic model, source definitions only) ----

    private void CollectDeclarations(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            CollectType(type);
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            CollectDeclarations(child);
        }
    }

    private void CollectType(INamedTypeSymbol type)
    {
        if ((type.TypeKind == TypeKind.Interface || type.TypeKind == TypeKind.Delegate)
            && type.Locations.Any(location => location.IsInSource))
        {
            var container = type.TypeKind == TypeKind.Interface
                ? VarianceContainer.Interface
                : VarianceContainer.Delegate;
            foreach (var parameter in type.TypeParameters)
            {
                if (parameter.Variance == Microsoft.CodeAnalysis.VarianceKind.None)
                {
                    continue;
                }

                var location = parameter.Locations.FirstOrDefault(location => location.IsInSource)
                    ?? type.Locations.First(location => location.IsInSource);
                var span = location.GetLineSpan().StartLinePosition;
                _declarations.Add(new VariantDeclarationRecord(
                    Container: container,
                    TypeName: type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    TypeParameterName: parameter.Name,
                    Kind: VariancePositionFacts.ToKind(parameter.Variance),
                    Constraints: Array.Empty<string>(),
                    Origin: VarianceOrigin.Source,
                    BaselineHasErrors: _baseline.HasErrors(location.SourceTree),
                    FilePath: location.SourceTree?.FilePath,
                    Line: span.Line + 1,
                    Column: span.Character + 1));
            }
        }

        foreach (var nested in type.GetTypeMembers())
        {
            CollectType(nested);
        }
    }

    private static string Wildcard(UseFlow flow, string argument) => flow switch
    {
        UseFlow.Producer => $"? extends {argument}",
        UseFlow.Consumer => $"? super {argument}",
        _ => WildcardInvariant,
    };

    private static VarianceOrigin OriginOf(INamedTypeSymbol? type)
        => type != null && type.Locations.Any(location => location.IsInSource)
            ? VarianceOrigin.Source
            : VarianceOrigin.Metadata;

    // ---- ordering and statistics ----

    private void SortSites()
    {
        _declarations.Sort((a, b) => Compare(
            a.FilePath, a.Line, a.Column, a.TypeName + "|" + a.TypeParameterName,
            b.FilePath, b.Line, b.Column, b.TypeName + "|" + b.TypeParameterName));
        _uses.Sort((a, b) => Compare(
            a.FilePath, a.Line, a.Column, a.TypeDisplayName,
            b.FilePath, b.Line, b.Column, b.TypeDisplayName));
    }

    public static int Compare(
        string? fileA, int lineA, int columnA, string tieA,
        string? fileB, int lineB, int columnB, string tieB)
    {
        var file = string.CompareOrdinal(fileA ?? string.Empty, fileB ?? string.Empty);
        if (file != 0)
        {
            return file;
        }

        var line = lineA.CompareTo(lineB);
        if (line != 0)
        {
            return line;
        }

        var column = columnA.CompareTo(columnB);
        return column != 0 ? column : string.CompareOrdinal(tieA, tieB);
    }

    private void FillStatistics()
    {
        _statistics.VariantDeclarationsOutInterface =
            _declarations.Count(d => d.Container == VarianceContainer.Interface && d.Kind == VarianceKind.Out);
        _statistics.VariantDeclarationsInInterface =
            _declarations.Count(d => d.Container == VarianceContainer.Interface && d.Kind == VarianceKind.In);
        _statistics.VariantDeclarationsOutDelegate =
            _declarations.Count(d => d.Container == VarianceContainer.Delegate && d.Kind == VarianceKind.Out);
        _statistics.VariantDeclarationsInDelegate =
            _declarations.Count(d => d.Container == VarianceContainer.Delegate && d.Kind == VarianceKind.In);

        _statistics.MetadataVariantTypesUsed = _metadataVariantTypes.Count;
        _statistics.UseSitesTotal = _uses.Count;
        _statistics.UseSitesProducer = _uses.Count(u => u.Flow == UseFlow.Producer);
        _statistics.UseSitesConsumer = _uses.Count(u => u.Flow == UseFlow.Consumer);
        _statistics.UseSitesBoth = _uses.Count(u => u.Flow == UseFlow.Both);
        _statistics.NestedCompositionSites = _uses.Count(u => u.Tags.HasFlag(UseTag.NestedComposition));
    }

    /// <summary>
    /// Single-tree use-site collector. Owns the per-tree semantic model and the
    /// ancestor-query caches, so no semantic result is computed twice within a tree.
    /// </summary>
    private sealed class UseCollector
    {
        private readonly SemanticModel _model;
        private readonly bool _baseline;
        private readonly IReadOnlySet<string> _hintNames;
        private readonly HashSet<string> _typeParameterNames;
        private readonly List<VarianceUseRecord> _uses = new();
        private readonly HashSet<string> _metadataVariantTypes = new(StringComparer.Ordinal);

        // A use site may sit under several lambdas / dynamic invocations; resolve each
        // ancestor node at most once per tree.
        private readonly Dictionary<LambdaExpressionSyntax, bool> _expressionTreeLambda = new();
        private readonly Dictionary<InvocationExpressionSyntax, bool> _dynamicInvocation = new();

        private UseCollector(
            SemanticModel model,
            bool baseline,
            IReadOnlySet<string> hintNames,
            HashSet<string> typeParameterNames)
        {
            _model = model;
            _baseline = baseline;
            _hintNames = hintNames;
            _typeParameterNames = typeParameterNames;
        }

        public List<VarianceUseRecord> Uses => _uses;
        public HashSet<string> MetadataVariantTypes => _metadataVariantTypes;

        public static UseCollector Collect(
            Compilation compilation,
            SyntaxTree tree,
            VarianceBaselineCache baseline,
            IReadOnlySet<string> hintNames)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // Bare variant type parameters can only hide behind identifiers named in a
            // type-parameter list enclosing the position; gather those names syntactically.
            var typeParameterNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in root.DescendantNodes().OfType<TypeParameterSyntax>())
            {
                typeParameterNames.Add(parameter.Identifier.ValueText);
            }

            var collector = new UseCollector(
                model, baseline.HasErrors(tree), hintNames, typeParameterNames);
            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case MethodDeclarationSyntax method:
                        collector.RecordPosition(method.ReturnType);
                        break;
                    case LocalFunctionStatementSyntax local:
                        collector.RecordPosition(local.ReturnType);
                        break;
                    case DelegateDeclarationSyntax declaration:
                        collector.RecordPosition(declaration.ReturnType);
                        break;
                    case PropertyDeclarationSyntax property:
                        collector.RecordPosition(property.Type);
                        break;
                    case IndexerDeclarationSyntax indexer:
                        collector.RecordPosition(indexer.Type);
                        break;
                    case EventDeclarationSyntax eventDeclaration:
                        collector.RecordPosition(eventDeclaration.Type);
                        break;
                    case VariableDeclarationSyntax variable:
                        collector.RecordPosition(variable.Type);
                        break;
                    case ParameterSyntax parameter:
                        collector.RecordPosition(parameter.Type);
                        break;
                    case ForEachStatementSyntax forEach:
                        collector.RecordPosition(forEach.Type);
                        break;
                    case CatchDeclarationSyntax catchDeclaration when catchDeclaration.Type != null:
                        collector.RecordPosition(catchDeclaration.Type);
                        break;
                    case DeclarationExpressionSyntax declarationExpression:
                        collector.RecordPosition(declarationExpression.Type);
                        break;
                    case SimpleBaseTypeSyntax baseType:
                        collector.RecordPosition(baseType.Type);
                        break;
                    case PrimaryConstructorBaseTypeSyntax primaryBase:
                        collector.RecordPosition(primaryBase.Type);
                        break;
                    case ObjectCreationExpressionSyntax creation:
                        collector.RecordPosition(creation.Type);
                        break;
                    case CastExpressionSyntax cast:
                        collector.RecordPosition(cast.Type);
                        break;
                    case DefaultExpressionSyntax defaultExpression:
                        collector.RecordPosition(defaultExpression.Type);
                        break;
                    case TypeOfExpressionSyntax typeOf:
                        collector.RecordPosition(typeOf.Type);
                        break;
                    case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression)
                                                        || binary.IsKind(SyntaxKind.AsExpression):
                        if (binary.Right is TypeSyntax rightType)
                        {
                            collector.RecordPosition(rightType);
                        }

                        break;
                }
            }

            return collector;
        }

        private void RecordPosition(TypeSyntax? typeSyntax)
        {
            if (typeSyntax == null || !ShapeMightBeVariant(typeSyntax))
            {
                return;
            }

            var type = _model.GetTypeInfo(typeSyntax).Type;
            if (type == null)
            {
                return;
            }

            var positionFlow = VariancePositionFacts.FlowForPosition(typeSyntax);
            switch (type)
            {
                case ITypeParameterSymbol parameter
                    when parameter.Variance != Microsoft.CodeAnalysis.VarianceKind.None:
                    RecordBareTypeParameter(typeSyntax, parameter, positionFlow);
                    break;
                case IArrayTypeSymbol array when array.ElementType.IsReferenceType:
                    RecordArrayCovariance(typeSyntax, array);
                    break;
                case INamedTypeSymbol named when IsVariantClosedType(named):
                    RecordVariantClosedType(typeSyntax, named, positionFlow);
                    break;
            }
        }

        /// <summary>
        /// Syntax-only gate before any semantic query. A variance use can only appear at a
        /// reference array, a generic name from a known/source variant type, or an
        /// identifier naming an enclosing type parameter; binding every other position
        /// (int, string, tuples, ordinary generics, ...) is wasted work.
        /// </summary>
        private bool ShapeMightBeVariant(TypeSyntax type)
        {
            switch (type)
            {
                // Only reference-element arrays have C# array covariance; the
                // semantic pass rejects value arrays anyway. A jagged/multidimensional
                // type with 2+ rank specifiers ('int[][]') is stored flat by the parser
                // and its outer element is itself an array (always a reference type).
                case ArrayTypeSyntax array when array.RankSpecifiers.Count >= 2:
                    return true;
                case ArrayTypeSyntax array:
                    return ArrayElementCouldBeReference(array.ElementType);
                case NullableTypeSyntax nullable:
                    return ShapeMightBeVariant(nullable.ElementType);
                case GenericNameSyntax generic:
                    return _hintNames.Contains(generic.Identifier.ValueText);
                case IdentifierNameSyntax identifier:
                    // 'var' hides the actual type (an array or variant interface is only
                    // visible on the initializer), so it is resolved semantically.
                    return identifier.IsVar
                           || _typeParameterNames.Contains(identifier.Identifier.ValueText);
                case QualifiedNameSyntax qualified:
                    return ShapeMightBeVariant(qualified.Right);
                case AliasQualifiedNameSyntax alias:
                    return ShapeMightBeVariant(alias.Name);
                default:
                    return false;
            }
        }

        private static bool ArrayElementCouldBeReference(TypeSyntax element)
        {
            switch (element)
            {
                case ArrayTypeSyntax:
                    return true;
                // 'T?' is a value Nullable only when T is a value type; recurse so
                // 'int[]?[]' (annotated reference element) is still recognised.
                case NullableTypeSyntax nullable:
                    return ArrayElementCouldBeReference(nullable.ElementType);
                case PointerTypeSyntax:
                case TupleTypeSyntax:
                    return false;
                case PredefinedTypeSyntax predefined:
                    return predefined.Keyword.IsKind(SyntaxKind.StringKeyword)
                           || predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword);
                default:
                    // 'dynamic' reaches here as an IdentifierName and is a reference type.
                    return true;
            }
        }

        private static bool IsVariantClosedType(INamedTypeSymbol type)
            => type.TypeArguments.Length > 0
               && type.OriginalDefinition.TypeParameters.Any(
                   parameter => parameter.Variance != Microsoft.CodeAnalysis.VarianceKind.None);

        private void RecordBareTypeParameter(
            TypeSyntax typeSyntax,
            ITypeParameterSymbol parameter,
            UseFlow positionFlow)
        {
            var display = parameter.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var wildcard = Wildcard(positionFlow, display);
            var entry = new VarianceTypeArgumentUse(
                ParameterName: parameter.Name,
                DeclarationKind: VariancePositionFacts.ToKind(parameter.Variance),
                Flow: positionFlow,
                ArgumentType: display,
                SuggestedWildcard: wildcard);
            var chain = new[]
            {
                $"{VariancePositionFacts.FlowWord(positionFlow)}({VariancePositionFacts.FlowSymbol(positionFlow)}) ∘ "
                + $"{parameter.Name}<{VariancePositionFacts.VarianceAnnotation(parameter.Variance)}>",
            };
            AddUse(typeSyntax, new VarianceUseRecord(
                Flow: positionFlow,
                TypeDisplayName: display,
                SuggestedWildcard: wildcard,
                TypeArguments: new[] { entry },
                CompositionChain: chain,
                Tags: UseTag.None,
                EnclosedByExpressionTree: IsInExpressionTree(typeSyntax),
                EnclosedByDynamic: IsDynamic(typeSyntax),
                VarianceConversion: false,
                Origin: OriginOf(parameter.ContainingType),
                BaselineHasErrors: _baseline));
        }

        private void RecordArrayCovariance(TypeSyntax typeSyntax, IArrayTypeSymbol array)
        {
            // Reference-type array covariance is only registered (no conversion judgement in
            // batch 2); Flow stays conservative Both so no safety diagnostic is emitted.
            AddUse(typeSyntax, new VarianceUseRecord(
                Flow: UseFlow.Both,
                TypeDisplayName: array.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                SuggestedWildcard: WildcardInvariant,
                TypeArguments: Array.Empty<VarianceTypeArgumentUse>(),
                CompositionChain: Array.Empty<string>(),
                Tags: UseTag.None,
                EnclosedByExpressionTree: IsInExpressionTree(typeSyntax),
                EnclosedByDynamic: IsDynamic(typeSyntax),
                VarianceConversion: false,
                Origin: VarianceOrigin.Metadata,
                BaselineHasErrors: _baseline));
        }

        private void RecordVariantClosedType(
            TypeSyntax typeSyntax,
            INamedTypeSymbol type,
            UseFlow positionFlow)
        {
            var entries = new List<VarianceTypeArgumentUse>();
            var chain = new List<string>();
            var overall = Descend(type, positionFlow, entries, chain);
            var nested = entries.Count > 1;
            var origin = OriginOf(type);
            if (origin == VarianceOrigin.Metadata)
            {
                _metadataVariantTypes.Add(
                    type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }

            var wildcard = entries.Count > 0 ? entries[^1].SuggestedWildcard : WildcardInvariant;
            AddUse(typeSyntax, new VarianceUseRecord(
                Flow: overall,
                TypeDisplayName: type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                SuggestedWildcard: wildcard,
                TypeArguments: entries,
                CompositionChain: chain,
                Tags: nested ? UseTag.NestedComposition : UseTag.None,
                EnclosedByExpressionTree: IsInExpressionTree(typeSyntax),
                EnclosedByDynamic: IsDynamic(typeSyntax),
                VarianceConversion: false,
                Origin: origin,
                BaselineHasErrors: _baseline));
        }

        /// <summary>
        /// D7: descends the type-argument tree through variant layers, composing the incoming
        /// position flow with each layer's declared variance and recording the composition chain.
        /// </summary>
        private UseFlow Descend(
            ITypeSymbol type,
            UseFlow flow,
            List<VarianceTypeArgumentUse> entries,
            List<string> chain)
        {
            if (type is INamedTypeSymbol named
                && named.TypeArguments.Length > 0
                && named.OriginalDefinition.TypeParameters.Any(
                    parameter => parameter.Variance != Microsoft.CodeAnalysis.VarianceKind.None))
            {
                var original = named.OriginalDefinition;
                UseFlow? combined = null;
                for (var i = 0; i < named.TypeArguments.Length && i < original.TypeParameters.Length; i++)
                {
                    var parameter = original.TypeParameters[i];
                    var layerFlow = VariancePositionFacts.Compose(flow, parameter.Variance);
                    chain.Add(
                        $"{VariancePositionFacts.FlowWord(flow)}({VariancePositionFacts.FlowSymbol(flow)}) ∘ "
                        + $"{named.Name}<{VariancePositionFacts.VarianceAnnotation(parameter.Variance)} {parameter.Name}> = "
                        + $"{VariancePositionFacts.FlowWord(layerFlow)}");

                    var argument = named.TypeArguments[i];
                    if (parameter.Variance != Microsoft.CodeAnalysis.VarianceKind.None)
                    {
                        var argumentText = argument.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                        entries.Add(new VarianceTypeArgumentUse(
                            ParameterName: parameter.Name,
                            DeclarationKind: VariancePositionFacts.ToKind(parameter.Variance),
                            Flow: layerFlow,
                            ArgumentType: argumentText,
                            SuggestedWildcard: Wildcard(layerFlow, argumentText)));
                    }

                    var childFlow = Descend(argument, layerFlow, entries, chain);
                    combined = combined == null
                        ? childFlow
                        : VariancePositionFacts.Combine(combined.Value, childFlow);
                }

                return combined ?? flow;
            }

            return flow;
        }

        private void AddUse(TypeSyntax typeSyntax, VarianceUseRecord record)
        {
            var span = typeSyntax.GetLocation().GetLineSpan().StartLinePosition;
            _uses.Add(record with
            {
                FilePath = _model.SyntaxTree.FilePath,
                Line = span.Line + 1,
                Column = span.Character + 1,
            });
        }

        private bool IsInExpressionTree(SyntaxNode node)
        {
            foreach (var lambda in node.Ancestors().OfType<LambdaExpressionSyntax>())
            {
                if (!_expressionTreeLambda.TryGetValue(lambda, out var isExpressionTree))
                {
                    isExpressionTree = ExpressionTreeFacts.IsExpressionTreeLambda(_model, lambda);
                    _expressionTreeLambda[lambda] = isExpressionTree;
                }

                if (isExpressionTree)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsDynamic(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == "dynamic")
                {
                    return true;
                }

                if (ancestor is InvocationExpressionSyntax invocation)
                {
                    if (!_dynamicInvocation.TryGetValue(invocation, out var isDynamic))
                    {
                        isDynamic = _model.GetTypeInfo(invocation).Type is IDynamicTypeSymbol;
                        _dynamicInvocation[invocation] = isDynamic;
                    }

                    if (isDynamic)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
