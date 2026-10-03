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
internal sealed class VarianceSiteInventory
{
    private const string WildcardInvariant = "invariant";

    private readonly Compilation _compilation;
    private readonly VarianceAuditStatistics _statistics;
    private readonly Dictionary<string, bool> _baselineByFile = new(StringComparer.Ordinal);
    private readonly HashSet<string> _metadataVariantTypes = new(StringComparer.Ordinal);
    private readonly List<VariantDeclarationRecord> _declarations = new();
    private readonly List<VarianceUseRecord> _uses = new();

    public VarianceSiteInventory(Compilation compilation, VarianceAuditStatistics statistics)
    {
        _compilation = compilation;
        _statistics = statistics;
    }

    public VarianceAuditAnalysis Build()
    {
        CollectDeclarations(_compilation.Assembly.GlobalNamespace);
        foreach (var tree in _compilation.SyntaxTrees)
        {
            CollectUses(tree);
        }

        SortSites();
        FillStatistics();

        var analysis = new VarianceAuditAnalysis();
        analysis.DeclarationSites.AddRange(_declarations);
        analysis.UseSites.AddRange(_uses);
        return analysis;
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
                    BaselineHasErrors: GetBaselineErrors(location.SourceTree),
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

    // ---- use sites (§4.3: one record per syntactic type position) ----

    private void CollectUses(SyntaxTree tree)
    {
        var model = _compilation.GetSemanticModel(tree);
        var baseline = GetBaselineErrors(tree);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    RecordPosition(model, method.ReturnType, baseline);
                    break;
                case LocalFunctionStatementSyntax local:
                    RecordPosition(model, local.ReturnType, baseline);
                    break;
                case DelegateDeclarationSyntax declaration:
                    RecordPosition(model, declaration.ReturnType, baseline);
                    break;
                case PropertyDeclarationSyntax property:
                    RecordPosition(model, property.Type, baseline);
                    break;
                case IndexerDeclarationSyntax indexer:
                    RecordPosition(model, indexer.Type, baseline);
                    break;
                case EventDeclarationSyntax eventDeclaration:
                    RecordPosition(model, eventDeclaration.Type, baseline);
                    break;
                case VariableDeclarationSyntax variable:
                    RecordPosition(model, variable.Type, baseline);
                    break;
                case ParameterSyntax parameter:
                    RecordPosition(model, parameter.Type, baseline);
                    break;
                case ForEachStatementSyntax forEach:
                    RecordPosition(model, forEach.Type, baseline);
                    break;
                case CatchDeclarationSyntax catchDeclaration when catchDeclaration.Type != null:
                    RecordPosition(model, catchDeclaration.Type, baseline);
                    break;
                case DeclarationExpressionSyntax declarationExpression:
                    RecordPosition(model, declarationExpression.Type, baseline);
                    break;
                case SimpleBaseTypeSyntax baseType:
                    RecordPosition(model, baseType.Type, baseline);
                    break;
                case PrimaryConstructorBaseTypeSyntax primaryBase:
                    RecordPosition(model, primaryBase.Type, baseline);
                    break;
                case ObjectCreationExpressionSyntax creation:
                    RecordPosition(model, creation.Type, baseline);
                    break;
                case CastExpressionSyntax cast:
                    RecordPosition(model, cast.Type, baseline);
                    break;
                case DefaultExpressionSyntax defaultExpression:
                    RecordPosition(model, defaultExpression.Type, baseline);
                    break;
                case TypeOfExpressionSyntax typeOf:
                    RecordPosition(model, typeOf.Type, baseline);
                    break;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression)
                                                        || binary.IsKind(SyntaxKind.AsExpression):
                    if (binary.Right is TypeSyntax rightType)
                    {
                        RecordPosition(model, rightType, baseline);
                    }

                    break;
            }
        }
    }

    private void RecordPosition(SemanticModel model, TypeSyntax? typeSyntax, bool baseline)
    {
        if (typeSyntax == null)
        {
            return;
        }

        var type = model.GetTypeInfo(typeSyntax).Type;
        if (type == null)
        {
            return;
        }

        var positionFlow = VariancePositionFacts.FlowForPosition(typeSyntax);
        switch (type)
        {
            case ITypeParameterSymbol parameter
                when parameter.Variance != Microsoft.CodeAnalysis.VarianceKind.None:
                RecordBareTypeParameter(model, typeSyntax, parameter, positionFlow, baseline);
                break;
            case IArrayTypeSymbol array when array.ElementType.IsReferenceType:
                RecordArrayCovariance(model, typeSyntax, array, baseline);
                break;
            case INamedTypeSymbol named when IsVariantClosedType(named):
                RecordVariantClosedType(model, typeSyntax, named, positionFlow, baseline);
                break;
        }
    }

    private static bool IsVariantClosedType(INamedTypeSymbol type)
        => type.TypeArguments.Length > 0
           && type.OriginalDefinition.TypeParameters.Any(
               parameter => parameter.Variance != Microsoft.CodeAnalysis.VarianceKind.None);

    private void RecordBareTypeParameter(
        SemanticModel model,
        TypeSyntax typeSyntax,
        ITypeParameterSymbol parameter,
        UseFlow positionFlow,
        bool baseline)
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
        AddUse(model, typeSyntax, new VarianceUseRecord(
            Flow: positionFlow,
            TypeDisplayName: display,
            SuggestedWildcard: wildcard,
            TypeArguments: new[] { entry },
            CompositionChain: chain,
            Tags: UseTag.None,
            EnclosedByExpressionTree: IsInExpressionTree(model, typeSyntax),
            EnclosedByDynamic: IsDynamic(model, typeSyntax),
            VarianceConversion: false,
            Origin: OriginOf(parameter.ContainingType),
            BaselineHasErrors: baseline));
    }

    private void RecordArrayCovariance(
        SemanticModel model,
        TypeSyntax typeSyntax,
        IArrayTypeSymbol array,
        bool baseline)
    {
        // Reference-type array covariance is only registered (no conversion judgement in
        // batch 2); Flow stays conservative Both so no safety diagnostic is emitted.
        AddUse(model, typeSyntax, new VarianceUseRecord(
            Flow: UseFlow.Both,
            TypeDisplayName: array.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            SuggestedWildcard: WildcardInvariant,
            TypeArguments: Array.Empty<VarianceTypeArgumentUse>(),
            CompositionChain: Array.Empty<string>(),
            Tags: UseTag.None,
            EnclosedByExpressionTree: IsInExpressionTree(model, typeSyntax),
            EnclosedByDynamic: IsDynamic(model, typeSyntax),
            VarianceConversion: false,
            Origin: VarianceOrigin.Metadata,
            BaselineHasErrors: baseline));
    }

    private void RecordVariantClosedType(
        SemanticModel model,
        TypeSyntax typeSyntax,
        INamedTypeSymbol type,
        UseFlow positionFlow,
        bool baseline)
    {
        var entries = new List<VarianceTypeArgumentUse>();
        var chain = new List<string>();
        var overall = Descend(type, positionFlow, entries, chain);
        var nested = entries.Count > 1;
        var origin = OriginOf(type);
        if (origin == VarianceOrigin.Metadata)
        {
            _metadataVariantTypes.Add(type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        var wildcard = entries.Count > 0 ? entries[^1].SuggestedWildcard : WildcardInvariant;
        AddUse(model, typeSyntax, new VarianceUseRecord(
            Flow: overall,
            TypeDisplayName: type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            SuggestedWildcard: wildcard,
            TypeArguments: entries,
            CompositionChain: chain,
            Tags: nested ? UseTag.NestedComposition : UseTag.None,
            EnclosedByExpressionTree: IsInExpressionTree(model, typeSyntax),
            EnclosedByDynamic: IsDynamic(model, typeSyntax),
            VarianceConversion: false,
            Origin: origin,
            BaselineHasErrors: baseline));
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

    private void AddUse(SemanticModel model, TypeSyntax typeSyntax, VarianceUseRecord record)
    {
        var span = typeSyntax.GetLocation().GetLineSpan().StartLinePosition;
        _uses.Add(record with
        {
            FilePath = model.SyntaxTree.FilePath,
            Line = span.Line + 1,
            Column = span.Character + 1,
        });
    }

    private static bool IsInExpressionTree(SemanticModel model, SyntaxNode node)
    {
        foreach (var lambda in node.Ancestors().OfType<LambdaExpressionSyntax>())
        {
            if (ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDynamic(SemanticModel model, SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == "dynamic")
            {
                return true;
            }

            if (ancestor is InvocationExpressionSyntax invocation
                && model.GetTypeInfo(invocation).Type is IDynamicTypeSymbol)
            {
                return true;
            }
        }

        return false;
    }

    // ---- baseline errors (D12) ----

    private bool GetBaselineErrors(SyntaxTree? tree)
    {
        if (tree == null)
        {
            return false;
        }

        if (_baselineByFile.TryGetValue(tree.FilePath, out var cached))
        {
            return cached;
        }

        var model = _compilation.GetSemanticModel(tree);
        var hasErrors = VariancePositionFacts.HasBaselineErrors(model);
        _baselineByFile[tree.FilePath] = hasErrors;
        return hasErrors;
    }

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
}