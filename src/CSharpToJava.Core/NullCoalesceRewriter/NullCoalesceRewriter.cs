using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <summary>
/// C# null-coalesce preprocessor public entry point. Rewrites ?? and ??=
/// operators into equivalent explicit ternary / if-assignment forms.
/// Clean files are byte-level preserved. Rewritten output is valid C#.
/// </summary>
public sealed class NullCoalesceRewriter
{
    public NullCoalesceResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        NullCoalesceOptions? options = null)
    {
        options ??= new NullCoalesceOptions();
        var stats = new NullCoalesceStatistics { FilesScanned = 1 };
        var diagnostics = new List<NullCoalesceDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // Dirty check: no ?? or ??= → byte-level copy
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new NullCoalesceResult(sourceCode, false, diagnostics, stats);
        }

        // Expression-bodied ('=>') members provide no statement slot for the
        // hoisted guards. Expand only the arrow bodies that actually contain a
        // ?? / ??= into equivalent block bodies ('{ return e; }' / '{ e; }'),
        // then rebind the semantic model to the expanded tree. Field/property
        // initializers have no host block and intentionally stay residual.
        var workingRoot = root;
        var workingModel = semanticModel;
        var expander = new ExpressionBodyExpander(semanticModel);
        if (expander.Visit(root) is CompilationUnitSyntax expandedRoot && expander.Expansions > 0)
        {
            workingRoot = expandedRoot;
            stats.ExpressionBodiesExpanded = expander.Expansions;
            if (semanticModel != null)
            {
                var originalTree = semanticModel.SyntaxTree;
                var expandedTree = CSharpSyntaxTree.Create(
                    expandedRoot,
                    (CSharpParseOptions?)originalTree.Options,
                    originalTree.FilePath);
                var reboundCompilation = semanticModel.Compilation.ReplaceSyntaxTree(
                    originalTree, expandedTree);
                // Re-fetch the root FROM the attached tree so every node the
                // rewriter walks belongs to the tree the semantic model is
                // bound to (model lookups require node/tree identity).
                workingRoot = expandedTree.GetCompilationUnitRoot();
                workingModel = reboundCompilation.GetSemanticModel(expandedTree);
            }
        }

        // Single-pass rewrite: expressions are rewritten bottom-up while
        // VisitBlock hoists the guard statements registered by each statement
        // directly before that (rewritten) statement.
        var rewriter = new NullCoalesceSyntaxRewriter(
            workingModel, options, stats, diagnostics);
        var rewrittenRoot = (CompilationUnitSyntax)rewriter.Visit(workingRoot)!;

        // Format
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(rewrittenRoot, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;

        // Post-check: residual ?? / ??=
        // W-06 (upgrade plan §4.3/G-18, sequential-failure policy): a residual
        // site whose subtree carries a collection expression is OWNED
        // downstream — expression-desugar (C36) lowers the collection syntax
        // and the converter's coalesce path renders the operator — so it is
        // deferred with a Warning instead of aborting the chain as a strict
        // Error before the later passes (and the generic passes) ever run.
        var residualNodes = ListResidualNullCoalesceNodes(code);
        if (residualNodes.Count > 0)
        {
            stats.ResidualCount = residualNodes.Count;
            stats.DeferredDownstreamSites = residualNodes.Count(IsOwnedDownstream);
            if (stats.DeferredDownstreamSites > 0)
            {
                var lines = string.Join(", ", residualNodes
                    .Where(IsOwnedDownstream)
                    .Select(LineNumber)
                    .Take(5));
                diagnostics.Add(new NullCoalesceDiagnostic(
                    NullCoalesceSeverity.Warning,
                    $"{stats.DeferredDownstreamSites} ??/??= site(s) deferred to later passes " +
                    "(the collection expression is owned by expression-desugar; the converter's " +
                    $"coalesce path renders the operator); line(s): {lines}."));
            }

            var strictResidual = stats.ResidualCount - stats.DeferredDownstreamSites;
            if (strictResidual > 0)
            {
                var severity = options.Strict
                    ? NullCoalesceSeverity.Error
                    : NullCoalesceSeverity.Warning;
                diagnostics.Add(new NullCoalesceDiagnostic(
                    severity,
                    $"{strictResidual} residual ??/??= operator(s) remaining after preprocessing."));
            }
        }

        return new NullCoalesceResult(code, true, diagnostics, stats);
    }

    /// <summary>
    /// W-06 classifier, shared by this pass's per-file post-check and the
    /// adapter's whole-tree gate (one implementation — the G-41 lesson): true
    /// when the residual coalesce site's subtree carries a collection
    /// expression, which expression-desugar (C36) owns and lowers later in
    /// the chain.
    /// </summary>
    internal static bool IsOwnedDownstream(SyntaxNode residualSite)
        => residualSite.DescendantNodesAndSelf()
            .Any(n => n is CollectionExpressionSyntax);

    private static int LineNumber(SyntaxNode node)
    {
        try
        {
            return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The residual ??/??= nodes of a (rewritten) source text.</summary>
    internal static List<SyntaxNode> ListResidualNullCoalesceNodes(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes()
            .Where(n =>
                n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
                || n is AssignmentExpressionSyntax a
                    && a.IsKind(SyntaxKind.CoalesceAssignmentExpression))
            .ToList();
    }

    /// <summary>Dirty check: syntax tree contains any CoalesceExpression or CoalesceAssignmentExpression.</summary>
    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
            || n is AssignmentExpressionSyntax a
                && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));

    /// <summary>Post-check: rewritten output still contains ?? or ??=.</summary>
    internal static bool ContainsResidualNullCoalesce(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        return IsDirty(root);
    }

    private static int CountResidual(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().Count(n =>
            n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
            || n is AssignmentExpressionSyntax a
                && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));
    }
}
