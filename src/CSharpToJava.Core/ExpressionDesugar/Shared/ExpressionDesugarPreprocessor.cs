using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
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
/// <item>AdhocWorkspace formatting; OutputCode + Changed=true.</item>
/// </list>
/// Without a semantic model a dirty file is safety-skipped with a warning
/// diagnostic instead of crashing.
/// </summary>
public sealed class ExpressionDesugarPreprocessor
{
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

        var activeRules = ExpressionDesugarRuleSet.Rules
            .Where(rule => rule.IsPotentiallyDirty(root, options))
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

        if (rewriteCount == 0)
        {
            // Dirty syntax but every site is diagnostic-only/intentionally
            // untouched (e.g. CE-GUARD-only files or all-warning skips): keep
            // the source bytes.
            stats.FilesDirtyNoRewrite = 1;
            stats.CollectionExpr.FilesDirtyNoRewrite = 1;
            if (interpolationActive)
                stats.Interpolations.FilesDirtyNoRewrite = 1;
            stats.Initializers.FilesDirtyNoRewrite = 1;
            return new ExpressionDesugarResult(sourceCode, false, diagnostics, stats);
        }

        // Single family-wide lifting pass (statement sequences, __ed_ numbering).
        currentRoot = ExpressionDesugarTempLifter.Apply(
            currentRoot, allPending, allAnnotations);
        stats.TempsLifted = allPending.Count;
        stats.CollectionExpr.TempsLifted = allPending.Count;
        if (interpolationActive)
            stats.Interpolations.TempsLifted = allPending.Count;
        stats.Initializers.TempsLifted = allPending.Count;

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

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(currentRoot, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;
        stats.CollectionExpr.FilesTransformed = 1;
        if (interpolationActive)
            stats.Interpolations.FilesTransformed = 1;
        stats.Initializers.FilesTransformed = 1;
        return new ExpressionDesugarResult(code, true, diagnostics, stats);
    }
}
