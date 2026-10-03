using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// The single C32 stage (design §4.8; C45 appends more stages without a new
/// pass id). Applies design §4.1 on one CompilationUnit:
/// Analyze → [AuditOnly: rows, stop] → freeze → Plan → Generate/Rewrite.
/// Statistics are mutated in place; diagnostics accumulate on the shared
/// list. The stage never formats — the facade runs Formatter (E02 shape).
/// </summary>
internal sealed class HolderLiftStage : IClosureLiftStage
{
    public string Id => "holder-lift";

    public ClosureStageResult Apply(ClosureStageInput input)
    {
        var (root, model, options, naming, statistics, diagnostics) = input;

        // 1. Analyze — read only. M1 behavior preserved exactly, including
        //    FilesSkippedClean when no candidate exists.
        var analysis = new CaptureAnalyzer(model, options, diagnostics).Analyze(root);

        if (analysis.AllSymbols.Count == 0)
        {
            statistics.FilesSkippedClean += 1;
            return new ClosureStageResult(root, false);
        }

        // 2. AuditOnly — prospective rows, no plans, no tree edits (M1 parity).
        if (options.AuditOnly)
        {
            HolderPlanner.EmitAuditRows(analysis, model, naming, statistics, diagnostics);
            return new ClosureStageResult(root, false);
        }

        // 3. Plan (freezes names; emits CS2J3204 boundary rows if any).
        var groups = HolderPlanner.BuildPlans(analysis, model, options, naming, diagnostics);

        // 4. Freeze: ANY blocking row keeps the file verbatim regardless of
        //    Strict; Strict=false only downgrades row severity (analyzer side).
        if (diagnostics.Any(d => d.Code != ClosureLiftCodes.AuditRow))
            return new ClosureStageResult(root, false);

        if (groups.Count == 0)
        {
            statistics.FilesSkippedClean += 1;
            return new ClosureStageResult(root, false);
        }

        // 5. Generate + Rewrite. One bottom-up traversal: statement splices and
        //    identifier sites resolve against ORIGINAL node identities, and
        //    holder classes are appended into host types during that same
        //    traversal (a separate ReplaceNodes step would rebuild the host
        //    subtree wrappers and invalidate the frozen identity tables).
        var rewriter = new CaptureSiteRewriter(groups, statistics);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;

        // Fail loud rather than emitting an unreferenced holder class.
        if (rewriter.SitesRewritten == 0)
            throw new InvalidOperationException(
                "C32: plans were frozen but no capture site was rewritten; " +
                "refusing to emit a dangling holder class.");

        // C45 M2 (design §4.1): publish the frozen groups so the consolidation
        // stage merges read-only/this captures into the SAME classes instead of
        // re-deriving the mutable classification (single FindWriteSites口径).
        if (input.PlanBag is { } bag)
            bag.HolderGroups.AddRange(groups);

        statistics.HolderClassesGenerated += groups.Count(g => !g.ArrayForm);
        statistics.HolderInstancesGenerated += groups.Count(g => !g.ArrayForm);
        // C45 M2: same-value alias (display class = the evolved C32 holder).
        statistics.DisplayClassesGenerated += groups.Count(g => !g.ArrayForm);
        statistics.CapturesLifted += groups.Sum(g => g.Symbols.Count);

        foreach (var reason in groups.SelectMany(g => g.Symbols).Select(s => s.Reason))
        {
            statistics.CapturesByReason[reason] =
                statistics.CapturesByReason.TryGetValue(reason, out var n) ? n + 1 : 1;
        }
        // SitesRewritten and ForeachPerIterationGroups are counted by the
        // rewriter itself (one rewriter per file → exact, no double counting).

        return new ClosureStageResult(rewritten, true);
    }
}
