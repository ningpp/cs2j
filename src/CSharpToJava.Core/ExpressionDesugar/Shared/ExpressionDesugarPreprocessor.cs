using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using CSharpToJava.Core.ExpressionDesugar.Interpolations;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Public entry point of the <c>expression-desugar</c> family.
/// Pipeline (design §4.1):
/// <list type="number">
/// <item>clean files (no rule's dirty gate matches) are byte-level preserved
/// (Changed=false, the adapter performs the verbatim copy incl. BOM);</item>
/// <item>Pass 1 Analyze: every active rule classifies its sites, builds plans,
/// accumulates statistics and emits diagnostics. Under strict options any
/// Error diagnostic makes the whole file atomically return Changed=false
/// (hard invariant #5); under non-strict options Error-class codes degrade to
/// Warnings and the affected sites are preserved while other sites rewrite;</item>
/// <item>Pass 2 Rewrite: each rule's bottom-up rewriter runs in fixed family
/// order (inner nodes expand first), followed by a single
/// <see cref="ExpressionDesugarTempLifter.Apply"/> (shared <c>__ed_</c>
/// numbering, statement sequences merged across rules);</item>
/// <item>residual gate (CE-12): unpaired leftover trigger nodes are 0589
/// (strict aborts the file; non-strict warns but keeps the other rewrites);</item>
/// <item>AdhocWorkspace formatting limited to the spans that actually changed
/// (whole-file <c>Formatter.Format</c> is quadratic on multi-hundred-KB files;
/// untouched subtrees are shared instances and stay byte-identical);
/// OutputCode + Changed=true.</item>
/// </list>
/// Without a semantic model a dirty file is safety-skipped with a warning
/// diagnostic instead of crashing.
/// </summary>
public sealed class ExpressionDesugarPreprocessor
{
    /// <summary>
    /// Ranges longer than this are checked for a rebuilt bulk node before being
    /// handed to the formatter; see <see cref="MergeSpans"/>.
    /// </summary>
    private const int MaxFormattedSpanLength = 4096;

    /// <summary>
    /// Token budget for a range that reaches into a rebuilt bulk node; see
    /// <see cref="MergeSpans"/>.
    /// </summary>
    private const int MaxFormattedTokenCount = 128;

    /// <summary>
    /// A node whose direct child list exceeds this is treated as a rebuilt bulk
    /// node (huge array/collection initializer); see <see cref="HasHugeFanOut"/>.
    /// </summary>
    private const int MaxFormattedChildCount = 2048;

    public ExpressionDesugarResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        ExpressionDesugarOptions? options = null)
    {
        options ??= new ExpressionDesugarOptions();
        var stats = new ExpressionDesugarStatistics { FilesScanned = 1 };
        var diagnostics = new List<ExpressionDesugarDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // ONE shared traversal feeds every rule's cheap gate; the individual
        // rules used to each walk the whole tree (up to five node walks plus a
        // token walk per file), which dominated the pass on large clean files.
        var profile = ExpressionDesugarTriggerScan.Scan(root, sourceCode);
        var activeRules = ExpressionDesugarRuleSet.Rules
            .Where(rule => rule.IsPotentiallyDirty(profile, options))
            .ToList();

        // File-level section mirroring is gated per rule so a disabled rule
        // produces zero statistics/output differences (C23 is opt-in).
        var interpolationActive = activeRules
            .Any(static rule => rule.Id == "Interpolations");

        if (activeRules.Count == 0)
        {
            stats.FilesSkippedClean = 1;
            stats.CollectionExpr.FilesSkippedClean = 1;
            stats.Initializers.FilesSkippedClean = 1;
            return new ExpressionDesugarResult(
                sourceCode, false, Array.Empty<ExpressionDesugarDiagnostic>(), stats);
        }

        if (interpolationActive)
            stats.Interpolations.FilesScanned = 1;

        if (semanticModel == null)
        {
            // The family dispatches on SemanticModel ConvertedType; without a
            // model every rewrite decision would be a guess. Safe skip with an
            // explicit diagnostic (non-strict Warning) and unchanged bytes.
            stats.FilesDirtyNoRewrite = 1;
            stats.CollectionExpr.FilesDirtyNoRewrite = 1;
            if (interpolationActive)
                stats.Interpolations.FilesDirtyNoRewrite = 1;
            stats.Initializers.FilesDirtyNoRewrite = 1;
            diagnostics.Add(new ExpressionDesugarDiagnostic(
                ExpressionDesugarSeverity.Warning,
                CollectionExprDiagnosticCodes.CS2J0571,
                "A semantic model is required to classify collection-expression " +
                "sites; the file was left unchanged."));
            return new ExpressionDesugarResult(
                sourceCode, false, diagnostics, stats);
        }

        var context = new RuleAnalysisContext
        {
            Root = root,
            SemanticModel = semanticModel,
            Options = options,
            Statistics = stats,
            Diagnostics = diagnostics,
        };

        // ---- Pass 1: Analyze (every active rule, fixed family order) ----
        var plans = new List<ExpressionDesugarRulePlan>(activeRules.Count);
        foreach (var rule in activeRules)
            plans.Add(rule.Analyze(context));

        // Atomicity (design hard invariant #5): a strict Error from analysis
        // fails the whole file — no node is rewritten, bytes are preserved.
        if (diagnostics.Any(d => d.Severity == ExpressionDesugarSeverity.Error))
        {
            stats.FilesDirtyNoRewrite = 1;
            stats.CollectionExpr.FilesDirtyNoRewrite = 1;
            if (interpolationActive)
                stats.Interpolations.FilesDirtyNoRewrite = 1;
            stats.Initializers.FilesDirtyNoRewrite = 1;
            return new ExpressionDesugarResult(sourceCode, false, diagnostics, stats);
        }

        // ---- Pass 2: Rewrite (bottom-up, rule after rule) ----
        var currentRoot = root;
        var allPending = new List<ExpressionDesugarTempLifter.PendingTemp>();
        var allAnnotations = new Dictionary<SyntaxNode, SyntaxAnnotation>();
        var rewriteCount = 0;

        // Stable-key rules stamp their node markers on the shared tree in ONE
        // pass before any rewrite. The walk itself refreshes node instances
        // (unavoidable once ≥2 nodes change), but the markers survive every
        // later rewrite, which is what the per-rule rewriters resolve against.
        var stamps = new Dictionary<SyntaxNode, List<SyntaxAnnotation>>();
        foreach (var plan in plans)
            plan.ContributeStamps(stamps);
        if (stamps.Count > 0)
            currentRoot = StableMarkerStamper.Apply(currentRoot, stamps);

        foreach (var plan in plans)
        {
            currentRoot = plan.Rewrite(currentRoot);
            rewriteCount += plan.RewriteCount;
            allPending.AddRange(plan.PendingTemps);
            foreach (var pair in plan.AnchorAnnotations)
                allAnnotations[pair.Key] = pair.Value;
        }

        if (rewriteCount == 0 && plans.All(static plan => !plan.HasDeferredSites))
        {
            // Dirty syntax but every site is diagnostic-only/intentionally
            // untouched (e.g. CE-GUARD-only files or all-warning skips): keep
            // the source bytes. Anchor-deferred plans must fall through to the
            // late pass, which may still lower them or report their error.
            stats.FilesDirtyNoRewrite = 1;
            stats.CollectionExpr.FilesDirtyNoRewrite = 1;
            if (interpolationActive)
                stats.Interpolations.FilesDirtyNoRewrite = 1;
            stats.Initializers.FilesDirtyNoRewrite = 1;
            return new ExpressionDesugarResult(sourceCode, false, diagnostics, stats);
        }

        // Single family-wide lifting pass (statement sequences, __ed_ numbering).
        currentRoot = ExpressionDesugarTempLifter.Apply(
            currentRoot, allPending, allAnnotations, context.SemanticModel);
        var liftedCount = allPending.Count;

        // Post-lift late rewrites: planned sites that traveled INSIDE another
        // rule's pending statement sequences are reachable only now (the
        // primary rewrites ran while those sequences were still in memory).
        // Their late pending sequences are lifted by one additional pass.
        List<ExpressionDesugarTempLifter.PendingTemp>? latePending = null;
        Dictionary<SyntaxNode, SyntaxAnnotation>? lateAnnotations = null;
        foreach (var plan in plans)
        {
            var lateRoot = plan.LateRewriteAfterLift(currentRoot);
            if (lateRoot == null)
                continue;

            currentRoot = lateRoot;
            if (plan.LatePendingTemps.Count > 0)
                (latePending ??= new List<ExpressionDesugarTempLifter.PendingTemp>())
                    .AddRange(plan.LatePendingTemps);
            foreach (var pair in plan.LateAnchorAnnotations)
                (lateAnnotations ??= new Dictionary<SyntaxNode, SyntaxAnnotation>())
                    [pair.Key] = pair.Value;
        }

        if (latePending is { Count: > 0 })
        {
            currentRoot = ExpressionDesugarTempLifter.Apply(
                currentRoot, latePending, lateAnnotations!, context.SemanticModel);
            liftedCount += latePending.Count;
        }

        stats.TempsLifted = liftedCount;
        stats.CollectionExpr.TempsLifted = liftedCount;
        if (interpolationActive)
            stats.Interpolations.TempsLifted = liftedCount;
        stats.Initializers.TempsLifted = liftedCount;

        // ---- Per-rule residual gates (post-lift tree) ----
        var residualCount = 0;
        foreach (var plan in plans)
            residualCount += plan.CheckResiduals(context, currentRoot);
        stats.ResidualCount = residualCount;
        stats.CollectionExpr.ResidualCount = residualCount;
        if (interpolationActive)
            stats.Interpolations.ResidualCount = residualCount;

        // A strict residual gate failure is also atomic: no formatted output
        // leaves the pass. Non-strict keeps the other rewrites (Warning only).
        if (diagnostics.Any(d => d.Severity == ExpressionDesugarSeverity.Error))
        {
            stats.FilesDirtyNoRewrite = 1;
            stats.CollectionExpr.FilesDirtyNoRewrite = 1;
            if (interpolationActive)
                stats.Interpolations.FilesDirtyNoRewrite = 1;
            stats.Initializers.FilesDirtyNoRewrite = 1;
            return new ExpressionDesugarResult(sourceCode, false, diagnostics, stats);
        }

        // Whole-file Formatter.Format degrades to O(n²) on large files (a 470 KB
        // XmlCharType.cs took minutes). Only the ranges that actually changed are
        // formatted; everything else keeps its original bytes. Unchanged subtrees
        // are shared node instances (ReplaceNode reuses them), so this diff walk
        // is linear in the number of nodes.
        var changedSpans = new List<TextSpan>();
        CollectChangedSpans(root, currentRoot, changedSpans);

        string code;
        if (changedSpans.Count == 0)
        {
            // Defensive: the Changed path always rewrites at least one node.
            code = currentRoot.ToFullString();
        }
        else
        {
            var text = currentRoot.GetText();
            var spans = MergeSpans(changedSpans, text, currentRoot);
            using var workspace = new AdhocWorkspace();
            code = Formatter.Format(currentRoot, spans, workspace).ToFullString();
        }

        stats.FilesTransformed = 1;
        stats.CollectionExpr.FilesTransformed = 1;
        if (interpolationActive)
            stats.Interpolations.FilesTransformed = 1;
        stats.Initializers.FilesTransformed = 1;
        return new ExpressionDesugarResult(code, true, diagnostics, stats);
    }

    /// <summary>
    /// Appends the spans of <paramref name="rewritten"/> whose text differs from
    /// <paramref name="original"/> (trivia ignored). Reference equality is the
    /// fast path because <c>ReplaceNode</c> reuses every untouched subtree.
    /// Nodes keep recursing while their child lists line up; a kind change or a
    /// rewritten token flags the owning node, and an insertion/deletion only
    /// flags the inserted range (child lists are aligned from both ends so a
    /// lifted statement or builder method never drags its whole parent — up to
    /// the compilation unit — into the formatted range).
    /// </summary>
    private static void CollectChangedSpans(
        SyntaxNode original,
        SyntaxNode rewritten,
        List<TextSpan> spans)
    {
        if (ReferenceEquals(original, rewritten))
            return;

        // Text-equivalent (trivia-only difference): nothing to reformat.
        if (original.IsEquivalentTo(rewritten))
            return;

        if (original.RawKind != rewritten.RawKind)
        {
            spans.Add(rewritten.Span);
            return;
        }

        var originalChildren = original.ChildNodesAndTokens();
        var rewrittenChildren = rewritten.ChildNodesAndTokens();
        if (originalChildren.Count == rewrittenChildren.Count)
        {
            for (var i = 0; i < originalChildren.Count; i++)
            {
                var originalChild = originalChildren[i];
                var rewrittenChild = rewrittenChildren[i];
                if (originalChild.IsNode != rewrittenChild.IsNode)
                {
                    spans.Add(rewritten.Span);
                    return;
                }

                if (originalChild.IsNode)
                {
                    CollectChangedSpans(
                        originalChild.AsNode()!, rewrittenChild.AsNode()!, spans);
                    continue;
                }

                // A token-level edit (keyword/identifier/literal) cannot be
                // narrowed any further: format the owning node's span. Trivia
                // counts as part of the token here: a SyntaxFactory-built token
                // has no leading or trailing trivia and the missing space must
                // be reintroduced (e.g. `new` + `Dto` would glue to `newDto`).
                if (originalChild.ToFullString() != rewrittenChild.ToFullString())
                {
                    spans.Add(rewritten.Span);
                    return;
                }
            }

            return;
        }

        // Child lists differ (statements/members inserted or removed): align the
        // untouched prefix and suffix, then flag only the rewritten middle.
        var minCount = Math.Min(originalChildren.Count, rewrittenChildren.Count);
        var prefix = 0;
        while (prefix < minCount
            && SameChild(originalChildren[prefix], rewrittenChildren[prefix]))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < minCount - prefix
            && SameChild(
                originalChildren[originalChildren.Count - 1 - suffix],
                rewrittenChildren[rewrittenChildren.Count - 1 - suffix]))
        {
            suffix++;
        }

        var middleEnd = rewrittenChildren.Count - suffix;
        if (prefix < middleEnd)
        {
            for (var i = prefix; i < middleEnd; i++)
                spans.Add(rewrittenChildren[i].Span);
            return;
        }

        // Pure deletion: point at the seam so the affected line is reformatted.
        spans.Add(new TextSpan(
            prefix < rewrittenChildren.Count
                ? rewrittenChildren[prefix].SpanStart
                : rewritten.Span.End,
            0));
    }

    private static bool SameChild(SyntaxNodeOrToken original, SyntaxNodeOrToken rewritten)
        => original.Equals(rewritten)
            || (original.RawKind == rewritten.RawKind
                && original.IsNode == rewritten.IsNode
                && original.ToFullString() == rewritten.ToFullString());

    /// <summary>
    /// Sorts the collected spans, widens each one to whole lines (plus the leading
    /// trivia of its first token), merges overlapping ranges and trims the ones
    /// that reach into a rebuilt bulk node. Widening is required for correctness
    /// because the formatter leaves the trivia at a span's edge alone: a rewritten
    /// first token that lost its leading trivia would keep the old indentation (a
    /// lifted statement) or glue to the previous token (<c>=new Dto()</c>).
    /// Neighbouring ranges then overlap, hence the merge.
    /// </summary>
    private static List<TextSpan> MergeSpans(
        List<TextSpan> spans, SourceText text, SyntaxNode root)
    {
        spans.Sort(static (left, right) => left.Start != right.Start
            ? left.Start.CompareTo(right.Start)
            : left.Length.CompareTo(right.Length));

        var lines = text.Lines;
        var merged = new List<TextSpan>(spans.Count);
        foreach (var span in spans)
        {
            var startLine = lines.GetLineFromPosition(span.Start);
            var endLine = lines.GetLineFromPosition(
                span.End > span.Start ? span.End - 1 : span.Start);

            // Leading trivia of the first token can start on an earlier line
            // (the newline a lifted statement carries), so start there.
            var firstTokenStart = root.FindToken(span.Start).FullSpan.Start;
            var previousLine = startLine.LineNumber > 0
                ? lines[startLine.LineNumber - 1].Start
                : startLine.Start;
            var start = Math.Min(previousLine, firstTokenStart);
            var end = endLine.End;

            // Cap the Formatter input, but only for ranges reaching into a
            // rebuilt bulk node: Roslyn's formatter needs superlinear time there
            // (the 130 K-element collection expression of XmlCharType.cs turned a
            // whole-file format into minutes). The synthesized tokens always sit
            // at the head of such a node and the reused children keep their own
            // trivia, so formatting the leading lines is enough. Ordinary large
            // rewrites (a block full of lifted statements) stay fully formatted.
            if (end - start > MaxFormattedSpanLength && HasHugeFanOut(root, start, end))
                end = LimitToTokenBudget(root, start, end);

            var expanded = TextSpan.FromBounds(start, end);

            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (expanded.Start <= last.End)
                {
                    if (expanded.End > last.End)
                        merged[^1] = TextSpan.FromBounds(last.Start, expanded.End);
                    continue;
                }
            }

            merged.Add(expanded);
        }

        return merged;
    }

    /// <summary>
    /// True when the range contains a node with a very large direct child list.
    /// The formatter's cost explodes once a range reaches into such a rebuilt
    /// node (a 130 K-element initializer), while ordinary code — even a large
    /// block of lifted statements — stays linear.
    /// </summary>
    private static bool HasHugeFanOut(SyntaxNode root, int start, int end)
        => root.DescendantNodes(TextSpan.FromBounds(start, end))
            .Any(node => node.ChildNodesAndTokens().Count > MaxFormattedChildCount);

    /// <summary>
    /// Trims the range to <see cref="MaxFormattedTokenCount"/> tokens, keeping the
    /// head of the rebuilt node (where the synthesized tokens live) inside the
    /// formatted range.
    /// </summary>
    private static int LimitToTokenBudget(SyntaxNode root, int start, int end)
    {
        var count = 0;
        foreach (var token in root.DescendantTokens(TextSpan.FromBounds(start, end)))
        {
            if (++count == MaxFormattedTokenCount)
                return token.Span.End;
        }

        return end;
    }
}
