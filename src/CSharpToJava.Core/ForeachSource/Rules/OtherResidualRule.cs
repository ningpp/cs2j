using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// FS-3: the two non-tuple, non-instance-Deconstruct residual families:
/// <list type="bullet">
/// <item><b>Other</b> — extension-method Deconstruct or any unclassifiable
/// deconstruction element: CS2J5421 Warning (upgraded to Error in strict
/// mode, ruling 4 — no reduction is attempted);</item>
/// <item><b>KeyValuePair</b> — ordering anomaly: E07 should already have
/// lowered KVP foreach deconstruction, so a surviving site is reported as
/// CS2J5422 Warning (strict Error). KVP residuals are exempted from the
/// FS-5 residual gate (they count in <c>KvResidualSeen</c> only).</item>
/// </list>
/// Neither form is rewritten. CS2J5423 is deliberately never emitted
/// (ruling 1 folds instance-Deconstruct residuals into
/// <see cref="DeconstructResidualRule"/>).
/// </summary>
internal sealed class OtherResidualRule : IForeachSourceRule
{
    public ForeachSourceRuleOutcome? TryHandle(
        ForeachSourceSite site,
        ForeachSourceRewriteContext context,
        CommonForEachStatementSyntax visitedNode)
    {
        if (visitedNode is not ForEachVariableStatementSyntax)
        {
            return null;
        }

        switch (site.Family)
        {
            case ForeachSourceFamily.Other:
            {
                var severity = context.Options.Strict
                    ? ForeachSourceSeverity.Error
                    : ForeachSourceSeverity.Warning;
                context.Statistics.OtherResidualDiagnosed++;
                context.Statistics.ResidualDeconstructionSites++;
                context.Report(
                    severity,
                    PreprocessDiagnosticCodes.ForeachSourceOtherResidual,
                    "[FS-3] foreach deconstruction over an element without an instance " +
                    "Deconstruct (extension method or unsupported shape) is not lowered " +
                    "by foreach-source; the node is left unchanged.",
                    visitedNode);
                return ForeachSourceRuleOutcome.KeepUnchanged;
            }

            case ForeachSourceFamily.KeyValuePair:
            {
                var severity = context.Options.Strict
                    ? ForeachSourceSeverity.Error
                    : ForeachSourceSeverity.Warning;
                context.Statistics.KvResidualSeen++;
                // Gate-exempt (design §4.5): KVP residuals do not fail FS-5.
                context.Report(
                    severity,
                    PreprocessDiagnosticCodes.ForeachSourceKvResidual,
                    "[FS-3] foreach over a KeyValuePair deconstruction reached foreach-source " +
                    "as a residual (foreach-deconstruction was expected to lower it earlier " +
                    "in the chain); the node is left unchanged.",
                    visitedNode);
                return ForeachSourceRuleOutcome.KeepUnchanged;
            }

            default:
                return null;
        }
    }
}
