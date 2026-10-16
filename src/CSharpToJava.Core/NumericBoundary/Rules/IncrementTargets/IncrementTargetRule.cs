using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;

/// <summary>
/// C17 IncrementTargets family rule (Order=10 — runs before the C15 checked
/// sweep so expanded arithmetic nodes are seen by C15, design decision 17 /
/// §4.5 2026-10-01 implementation correction).
/// <para>
/// M1 flow: read-only classification over the original tree → decision-14
/// planning/safety gate (CS2J5332/CS2J5339 errors) → Pass1 read-modify-write
/// placeholder rewrite → Pass2 anchor hoisting → model rebind → IT-13 residual
/// gate (CS2J5341, strict → Error). Any Error lets NB-12 roll the whole file
/// back to the original bytes.
/// </para>
/// </summary>
internal sealed class IncrementTargetRule : INumericBoundaryRule
{
    public string Id => "increment-targets";

    public int Order => 10;

    public bool IsEnabled(NumericBoundaryOptions options) => options.EnableIncrementTargets;

    public NumericBoundaryRuleResult Execute(NumericBoundaryRuleContext context)
    {
        // Parse-only inputs carry no semantic model: classification is entirely
        // semantic-model bound, so the rule is a no-op there.
        if (context.SemanticModel == null)
        {
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        // Whole-cluster skip: C17 only classifies ++/-- nodes, so a file with no
        // increment/decrement token has no site, no statistics and no residual.
        // FindAllSites' full semantic traversal and the rewriter visit are both
        // provably empty-handed here.
        if (!context.Profile.HasIncrementOrDecrement)
        {
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        var analyzer = new IncrementTargetAnalyzer(
            context.SemanticModel, context.Options, context.GetSharedAnalyzer());
        var sites = analyzer.FindAllSites(context.Root);

        // No owned/classifiable site: the pass-1 rewriter (a full tree visit with
        // its own semantic queries) can only be a no-op, so skip it entirely.
        if (sites.Count == 0)
        {
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        var rewriter = new IncrementTargetRewriter(
            context.SemanticModel,
            context.Options,
            context.Stats.IncrementTargets,
            context.Diagnostics,
            sites);

        // Planning errors (illegal target / no safe anchor): never touch the
        // tree; NB-12 rolls the file back from the error diagnostic.
        if (rewriter.HasErrorDiagnostic)
        {
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        var visitedRoot = (CompilationUnitSyntax)rewriter.Visit(context.Root)!;
        if (!rewriter.MadeChanges)
        {
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        var newRoot = IncrementTargetLifter.Apply(visitedRoot, rewriter.Plans);

        // Rebind before the residual gate (and before the C15 sweep) so the
        // expanded nodes are classified on the post-hoist tree.
        var reboundModel = context.RebindModel(newRoot);
        if (reboundModel == null)
        {
            context.Diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Error,
                PreprocessDiagnosticCodes.NumericBoundaryBuildFailed,
                "numeric-boundary rule 'increment-targets' produced a tree that fails to reparse while " +
                "rebinding the semantic model; the original file was preserved " +
                $"({PreprocessDiagnosticCodes.NumericBoundaryBuildFailed})."));
            context.Stats.ErrorsByReason[PreprocessDiagnosticCodes.NumericBoundaryBuildFailed] = 1;
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        var reboundRoot = context.Root;
        ApplyResidualGate(
            reboundModel,
            reboundRoot,
            context.Options,
            context.Stats.IncrementTargets,
            context.Diagnostics,
            context.GetSharedAnalyzer());

        EmitInfo(context.Stats.IncrementTargets, context.Diagnostics);

        return new NumericBoundaryRuleResult(reboundRoot, MadeChanges: true);
    }

    /// <summary>
    /// IT-13: rescan the output tree for ++/-- sites that are still owned by
    /// C17. A clean expansion leaves none (the nodes are assignments/bare
    /// arithmetic); anything surviving is a normalization leak — CS2J5341,
    /// strict → Error, else Warning. Output inside the support-library
    /// fingerprint is exempt.
    /// </summary>
    private static void ApplyResidualGate(
        SemanticModel model,
        CompilationUnitSyntax root,
        NumericBoundaryOptions options,
        IncrementTargetStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics,
        NumericBoundaryAnalyzer sharedAnalyzer)
    {
        var reboundAnalyzer = new IncrementTargetAnalyzer(
            model, options, sharedAnalyzer);

        var residuals = new List<string>();
        foreach (var (node, classification) in reboundAnalyzer.FindAllSites(root))
        {
            if (!IncrementTargetAnalyzer.IsOwnedByRule(classification))
            {
                continue;
            }

            // Support-library fingerprint exemption (defensive; a rebound
            // classification is normally enough to keep these unowned).
            if (classification.Operand.ToString().Contains("Cs2jUnsigned", StringComparison.Ordinal)
                || classification.Operand.ToString().Contains("dotnet.System", StringComparison.Ordinal))
            {
                continue;
            }

            if (residuals.Count < 5)
            {
                residuals.Add(IncrementTargetSyntaxFacts.SiteText(node));
            }
        }

        if (residuals.Count == 0)
        {
            return;
        }

        var severity = options.Strict
            ? NumericBoundarySeverity.Error
            : NumericBoundarySeverity.Warning;
        diagnostics.Add(new NumericBoundaryDiagnostic(
            severity,
            PreprocessDiagnosticCodes.IncrementTargetResidual,
            IncrementTargetDiagnostics.Residual(string.Join(", ", residuals))));
        var key = PreprocessDiagnosticCodes.IncrementTargetResidual;
        stats.DeferredByReason[key] = stats.DeferredByReason.TryGetValue(key, out var count)
            ? count + 1
            : 1;
    }

    /// <summary>CS2J5330/CS2J5331 are aggregated, verbose-only Info diagnostics.</summary>
    private static void EmitInfo(
        IncrementTargetStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics)
    {
        var totalRewritten = stats.PropertySites
            + stats.IndexerSites
            + stats.SpecialArrayElementSites
            + stats.EnumCastSites
            + stats.UnsignedResidualSites
            + stats.DecimalSites
            + stats.RefAliasSites;
        if (totalRewritten == 0)
        {
            return;
        }

        diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Info,
            PreprocessDiagnosticCodes.IncrementTargetSiteInfo,
            IncrementTargetDiagnostics.SiteInfo(
                totalRewritten,
                stats.PropertySites,
                stats.IndexerSites,
                stats.SpecialArrayElementSites,
                stats.EnumCastSites,
                stats.UnsignedResidualSites,
                stats.PostfixLiftedSites,
                stats.UnsignedMathCallSites,
                stats.DecimalSites,
                stats.RefAliasSites)));

        // CS2J5334: one aggregated Info per file (C34 already merged —
        // Info, not Warning; open-question ruling 2).
        if (stats.DecimalSites > 0)
        {
            diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Info,
                PreprocessDiagnosticCodes.IncrementTargetDecimalInfo,
                IncrementTargetDiagnostics.DecimalExpandedInfo(stats.DecimalSites)));
        }

        if (stats.TempsByRole.Count > 0)
        {
            diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Info,
                PreprocessDiagnosticCodes.IncrementTargetTempInfo,
                IncrementTargetDiagnostics.TempInfo(stats.TempsByRole)));
        }
    }
}
