using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// FS-2 (ruling 2, long-term default): tuple-family deconstruction
/// residuals are reported as CS2J5420 Error and the node is not rewritten.
/// The locked chain runs E06 (tuple) and E07 (foreach-deconstruction)
/// before this pass, so any surviving tuple foreach deconstruction is a
/// hard gate failure rather than a silently-broken Java translation.
/// </summary>
internal sealed class TupleResidualRule : IForeachSourceRule
{
    public ForeachSourceRuleOutcome? TryHandle(
        ForeachSourceSite site,
        ForeachSourceRewriteContext context,
        CommonForEachStatementSyntax visitedNode)
    {
        if (visitedNode is not ForEachVariableStatementSyntax
            || site.Family != ForeachSourceFamily.Tuple)
        {
            return null;
        }

        context.Statistics.TupleResidualSeen++;
        context.Statistics.ResidualDeconstructionSites++;
        context.Report(
            ForeachSourceSeverity.Error,
            PreprocessDiagnosticCodes.ForeachSourceTupleResidual,
            "[FS-2] foreach over a tuple/ValueTuple deconstruction reached foreach-source " +
            "as a residual; tuple lowering (FS-2a/b/c) is intentionally not implemented. " +
            "The node is left unchanged.",
            visitedNode);

        return ForeachSourceRuleOutcome.KeepUnchanged;
    }
}
