using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.IndexRange;

/// <summary>
/// Pass 1: bottom-up rewriter for <c>^</c>/<c>..</c>. Element-access sites
/// are rewritten via <see cref="IndexRewriter"/>/<see cref="RangeRewriter"/>
/// (collecting single-evaluation temps); standalone values and System.Index/
/// System.Range declarations receive explicit IR120 diagnostics; unresolvable
/// receivers get IR110; non-hoistable contexts get IR130.
/// </summary>
internal sealed class IndexRangeSyntaxRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
{
    private readonly SemanticModel? _model;
    private readonly IndexRangeOptions _options;
    private readonly IndexRangeStatistics _stats;
    private readonly List<IndexRangeDiagnostic> _diagnostics;

    internal List<TempLifter.PendingTemp> Pending { get; } = new();

    private readonly TempNameAllocator _names;
    private int _rewriteCount;

    /// <summary>
    /// Maps each original anchor node to a unique annotation. The annotation
    /// is attached to the corresponding node in the rewritten tree so Pass 2
    /// (TempLifter) can locate insertion points despite tree reconstruction.
    /// </summary>
    internal IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
        => _anchorAnnotations;
    private readonly Dictionary<SyntaxNode, SyntaxAnnotation> _anchorAnnotations = new();
    private int _anchorCounter;

    public IndexRangeSyntaxRewriter(
        SemanticModel? model,
        IndexRangeOptions options,
        IndexRangeStatistics stats,
        List<IndexRangeDiagnostic> diagnostics,
        Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax root)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _names = new TempNameAllocator(root);
    }

    public override Microsoft.CodeAnalysis.SyntaxNode? VisitElementAccessExpression(
        ElementAccessExpressionSyntax node)
    {
        var visited = (ElementAccessExpressionSyntax)base
            .VisitElementAccessExpression(node)!;

        var positions = FindIndexRangeArguments(node);
        if (positions.Count == 0)
            return visited;

        if (positions.Count > 1)
        {
            // Multidimensional index/range: baseline-illegal in C#; invent nothing.
            _stats.AddSkip("baseline");
            return AnnotateArguments(
                visited, positions, IndexRangeSyntaxFacts.SkippedAnnotation);
        }

        var position = positions[0];

        if (_model == null)
        {
            Emit(new IndexRangeDiagnostic(
                "IR110",
                IndexRangeSeverity.Error,
                "Semantic model is required to classify the index/range receiver."));
            return AnnotateArguments(
                visited, positions, IndexRangeSyntaxFacts.CoveredAnnotation);
        }

        var info = ReceiverKindAnalyzer.Analyze(node, _model);

        if (info.Kind == ReceiverKind.MultiDim)
        {
            _stats.AddSkip("baseline");
            return AnnotateArguments(
                visited, positions, IndexRangeSyntaxFacts.SkippedAnnotation);
        }

        if (info.Kind == ReceiverKind.Pointer)
        {
            _stats.AddSkip("C06");
            return AnnotateArguments(
                visited, positions, IndexRangeSyntaxFacts.SkippedAnnotation);
        }

        if (info.Kind == ReceiverKind.Unknown)
        {
            Emit(new IndexRangeDiagnostic(
                "IR110",
                IndexRangeSeverity.Error,
                "Receiver type is unresolvable (dynamic/error type); cannot " +
                "determine the Length/Count member for index/range rewrite."));
            _stats.AddSkip("IR110");
            return AnnotateArguments(
                visited, positions, IndexRangeSyntaxFacts.CoveredAnnotation);
        }

        if (IndexRangeSyntaxFacts.IsConditionalAccessFace(node))
        {
            Emit(new IndexRangeDiagnostic(
                "IR130",
                IndexRangeSeverity.Error,
                "'arr?[^n]'/'arr?[..]' is the null-conditional syntax face " +
                "(C35 NullConditionalDesugar); the index-range pass does not " +
                "expand it."));
            _stats.AddSkip("IR130");
            return AnnotateArguments(
                visited, positions, IndexRangeSyntaxFacts.CoveredAnnotation);
        }

        var anchor = TempLifter.FindAnchor(node);
        var argument = visited.ArgumentList.Arguments[position].Expression;

        bool allPure;
        if (argument.RawKind == IndexRangeSyntaxFacts.IndexExpressionKind
            && argument is PrefixUnaryExpressionSyntax hat)
            allPure = IndexRangeSyntaxFacts.IsPure(visited.Expression)
                      && IndexRangeSyntaxFacts.IsPure(hat.Operand);
        else if (argument is RangeExpressionSyntax range)
            allPure = IndexRangeSyntaxFacts.IsPure(visited.Expression)
                      && SidePure(range.LeftOperand) && SidePure(range.RightOperand);
        else
            allPure = false;

        if (anchor == null)
        {
            // Zero-temp inline is valid in any context, even with no anchor.
            if (allPure)
            {
                return RewriteSite(
                    visited, argument, info, lift: false, anchor: null!);
            }

            string reason = "";
            var inNoTempContext = _model != null
                && IndexRangeSyntaxFacts.IsNoTempContext(
                    node, _model, out reason);

            if (inNoTempContext)
            {
                Emit(new IndexRangeDiagnostic(
                    "IR130",
                    IndexRangeSeverity.Error,
                    $"{reason} cannot host temp declarations; only constant/" +
                    "simple (zero-temp) index/range forms are allowed."));
            }
            else
            {
                Emit(new IndexRangeDiagnostic(
                    "IR130",
                    IndexRangeSeverity.Error,
                    "No statement anchor for temp declarations; only zero-temp " +
                    "index/range forms are allowed here."));
            }

            _stats.AddSkip("IR130");
            return AnnotateArguments(
                visited, positions, IndexRangeSyntaxFacts.CoveredAnnotation);
        }

        return RewriteSite(visited, argument, info, !allPure, anchor);
    }

    private Microsoft.CodeAnalysis.SyntaxNode RewriteSite(
        ElementAccessExpressionSyntax visited,
        ExpressionSyntax originalArgument,
        ReceiverInfo info,
        bool lift,
        Microsoft.CodeAnalysis.SyntaxNode anchor)
    {
        SyntaxNode rewrittenSite;
        if (originalArgument.RawKind == IndexRangeSyntaxFacts.IndexExpressionKind)
        {
            var hat = (PrefixUnaryExpressionSyntax)visited.ArgumentList
                .Arguments[0].Expression;
            _rewriteCount++;
            rewrittenSite = IndexRewriter.RewriteHatElement(
                visited, hat, info, lift, anchor, _names, Pending, _stats);
        }
        else
        {
            var range = (RangeExpressionSyntax)visited.ArgumentList.Arguments[0]
                .Expression;
            var replacement = RangeRewriter.RewriteRangeElement(
                visited, range, info, anchor, _names, Pending, _stats, _diagnostics);

            if (replacement == null)
            {
                var positions = new List<int> { 0 };
                return AnnotateArguments(
                    visited, positions, IndexRangeSyntaxFacts.CoveredAnnotation);
            }

            _rewriteCount++;
            rewrittenSite = replacement;
        }

        if (anchor != null)
            RegisterAnchor(anchor);

        return rewrittenSite;
    }

    public override Microsoft.CodeAnalysis.SyntaxNode? VisitElementBindingExpression(
        ElementBindingExpressionSyntax node)
    {
        var visited = (ElementBindingExpressionSyntax)
            base.VisitElementBindingExpression(node)!;

        var positions = FindPositions(node.ArgumentList);
        if (positions.Count == 0)
            return visited;

        Emit(new IndexRangeDiagnostic(
            "IR130",
            IndexRangeSeverity.Error,
            "'arr?[^n]'/'arr?[..]' is the null-conditional syntax face " +
            "(C35 NullConditionalDesugar); the index-range pass does not " +
            "expand it."));
        _stats.AddSkip("IR130");
        return AnnotateNodeArguments(
            visited, visited.ArgumentList, positions,
            IndexRangeSyntaxFacts.CoveredAnnotation);
    }

    public override Microsoft.CodeAnalysis.SyntaxNode? VisitPrefixUnaryExpression(
        PrefixUnaryExpressionSyntax node)
    {
        var visited = (PrefixUnaryExpressionSyntax)base
            .VisitPrefixUnaryExpression(node)!;

        if (node.RawKind != IndexRangeSyntaxFacts.IndexExpressionKind)
            return visited;

        if (IsElementAccessArgument(node))
            return visited;

        // C36 owns ranges/hats inside a collection-expression spread ([..]).
        if (IsSpreadOwned(node))
            return visited.WithAdditionalAnnotations(
                IndexRangeSyntaxFacts.SkippedAnnotation);

        Emit(new IndexRangeDiagnostic(
            "IR120",
            IndexRangeSeverity.Error,
            "Standalone from-end index '^n' (System.Index value) outside an " +
            "element-access use site is not supported; rewrite as an explicit " +
            "length-based int expression at the use site."));
        _stats.StandaloneIndexSites++;
        _stats.AddSkip("IR120");
        return visited.WithAdditionalAnnotations(
            IndexRangeSyntaxFacts.CoveredAnnotation);
    }

    public override Microsoft.CodeAnalysis.SyntaxNode? VisitRangeExpression(
        RangeExpressionSyntax node)
    {
        var visited = (RangeExpressionSyntax)base.VisitRangeExpression(node)!;

        if (IsElementAccessArgument(node))
            return visited;

        // C36 owns ranges inside a collection-expression spread ([..]).
        if (IsSpreadOwned(node))
            return visited.WithAdditionalAnnotations(
                IndexRangeSyntaxFacts.SkippedAnnotation);

        Emit(new IndexRangeDiagnostic(
            "IR120",
            IndexRangeSeverity.Error,
            "Standalone range expression (System.Range value) outside an " +
            "element-access use site is not supported; rewrite with explicit " +
            "int offset/count values at the use site."));
        _stats.StandaloneRangeSites++;
        _stats.AddSkip("IR120");
        return visited.WithAdditionalAnnotations(
            IndexRangeSyntaxFacts.CoveredAnnotation);
    }

    public override Microsoft.CodeAnalysis.SyntaxNode? VisitVariableDeclarator(
        VariableDeclaratorSyntax node)
    {
        var visited = (VariableDeclaratorSyntax)base.VisitVariableDeclarator(node)!;

        if (_model != null
            && node.Parent is VariableDeclarationSyntax declaration
            && !declaration.Type.IsVar
            && ReceiverKindAnalyzer.IsIndexOrRangeType(
                _model.GetTypeInfo(declaration.Type).Type))
        {
            var initializerHasIndexRange = node.Initializer != null
                && node.Initializer.Value
                    .DescendantNodesAndSelf()
                    .Any(IndexRangeSyntaxFacts.IsIndexOrRangeNode);
            if (!initializerHasIndexRange)
            {
                Emit(new IndexRangeDiagnostic(
                    "IR120",
                    IndexRangeSeverity.Error,
                    $"System.Index/System.Range variable '{node.Identifier.Text}' " +
                    "is not supported; replace it with explicit int offset/count " +
                    "values at each use site."));
                _stats.AddSkip("IR120");
            }
        }

        return visited;
    }

    public override Microsoft.CodeAnalysis.SyntaxNode? VisitParameter(
        ParameterSyntax node)
    {
        var visited = (ParameterSyntax)base.VisitParameter(node)!;

        if (_model != null
            && node.Type != null
            && ReceiverKindAnalyzer.IsIndexOrRangeType(
                _model.GetTypeInfo(node.Type).Type))
        {
            Emit(new IndexRangeDiagnostic(
                "IR120",
                IndexRangeSeverity.Error,
                $"System.Index/System.Range parameter '{node.Identifier.Text}' " +
                "is not supported; replace it with explicit int offset/count " +
                "parameters."));
            _stats.AddSkip("IR120");
        }

        return visited;
    }

    public override Microsoft.CodeAnalysis.SyntaxNode? VisitMethodDeclaration(
        MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        if (_model != null
            && ReceiverKindAnalyzer.IsIndexOrRangeType(
                _model.GetTypeInfo(node.ReturnType).Type))
        {
            Emit(new IndexRangeDiagnostic(
                "IR120",
                IndexRangeSeverity.Error,
                $"Method '{node.Identifier.Text}' returns System.Index/System.Range; " +
                "not supported. Return explicit int offset/count values instead."));
            _stats.AddSkip("IR120");
        }

        return visited;
    }

    public override Microsoft.CodeAnalysis.SyntaxNode? VisitPropertyDeclaration(
        PropertyDeclarationSyntax node)
    {
        var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;

        if (_model != null
            && ReceiverKindAnalyzer.IsIndexOrRangeType(
                _model.GetTypeInfo(node.Type).Type))
        {
            Emit(new IndexRangeDiagnostic(
                "IR120",
                IndexRangeSeverity.Error,
                $"System.Index/System.Range property '{node.Identifier.Text}' " +
                "is not supported; replace with explicit int offset/count values."));
            _stats.AddSkip("IR120");
        }

        return visited;
    }

    internal int RewriteCount => _rewriteCount;

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var result = base.Visit(node);
        if (node != null
            && result != null
            && _anchorAnnotations.TryGetValue(node, out var annotation))
        {
            result = result.WithAdditionalAnnotations(annotation);
        }

        return result;
    }

    private void RegisterAnchor(SyntaxNode anchor)
    {
        if (_anchorAnnotations.ContainsKey(anchor))
            return;

        _anchorCounter++;
        _anchorAnnotations[anchor] = new SyntaxAnnotation(
            "ir-anchor", _anchorCounter.ToString());
    }

    private static bool IsElementAccessArgument(Microsoft.CodeAnalysis.SyntaxNode node)
    {
        // A hat directly inside a range operand is covered when that range
        // itself is an element-access argument.
        var current = node;
        if (current.Parent is RangeExpressionSyntax outerRange)
            current = outerRange;

        return current.Parent is ArgumentSyntax argument
               && argument.Parent is BaseArgumentListSyntax argumentList
               && argumentList.Parent is
                   ElementAccessExpressionSyntax or ElementBindingExpressionSyntax;
    }

    private static bool IsSpreadOwned(Microsoft.CodeAnalysis.SyntaxNode node)
    {
        // A hat inside a range operand: that range may be the spread itself.
        var current = node;
        if (current.Parent is RangeExpressionSyntax range)
            current = range;

        return current.Parent is SpreadElementSyntax;
    }

    private static List<int> FindIndexRangeArguments(
        ElementAccessExpressionSyntax node)
        => FindPositions(node.ArgumentList);

    private static List<int> FindPositions(BaseArgumentListSyntax argumentList)
    {
        var result = new List<int>();
        for (var i = 0; i < argumentList.Arguments.Count; i++)
        {
            var expression = argumentList.Arguments[i].Expression;
            if (IndexRangeSyntaxFacts.IsIndexOrRangeNode(expression))
                result.Add(i);
        }
        return result;
    }

    private static bool SidePure(ExpressionSyntax? side)
    {
        if (side == null)
            return true;
        return side is PrefixUnaryExpressionSyntax index
               && index.RawKind == IndexRangeSyntaxFacts.IndexExpressionKind
            ? IndexRangeSyntaxFacts.IsPure(index.Operand)
            : IndexRangeSyntaxFacts.IsPure(side);
    }

    private static ElementAccessExpressionSyntax AnnotateArguments(
        ElementAccessExpressionSyntax elementAccess,
        IReadOnlyList<int> positions,
        SyntaxAnnotation annotation)
        => elementAccess.WithArgumentList(
            elementAccess.ArgumentList.WithArguments(
                AnnotatedArguments(
                    elementAccess.ArgumentList, positions, annotation)));

    private static ExpressionSyntax AnnotateNodeArguments(
        ExpressionSyntax node,
        BaseArgumentListSyntax argumentList,
        IReadOnlyList<int> positions,
        SyntaxAnnotation annotation)
    {
        var annotatedList = argumentList.WithArguments(
            AnnotatedArguments(argumentList, positions, annotation));
        return node switch
        {
            ElementAccessExpressionSyntax ea =>
                ea.WithArgumentList((BracketedArgumentListSyntax)annotatedList),
            ElementBindingExpressionSyntax eb =>
                eb.WithArgumentList((BracketedArgumentListSyntax)annotatedList),
            _ => node,
        };
    }

    private static SeparatedSyntaxList<ArgumentSyntax> AnnotatedArguments(
        BaseArgumentListSyntax argumentList,
        IReadOnlyList<int> positions,
        SyntaxAnnotation annotation)
    {
        var arguments = argumentList.Arguments;
        foreach (var position in positions)
        {
            var argument = arguments[position];
            arguments = arguments.Replace(
                argument,
                argument.WithExpression(
                    argument.Expression.WithAdditionalAnnotations(annotation)));
        }

        return arguments;
    }

    private void Emit(IndexRangeDiagnostic diagnostic)
        => _diagnostics.Add(diagnostic);
}
