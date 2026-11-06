namespace CSharpToJava.Core.NullCoalesceRewriter;

public sealed class NullCoalesceStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int NullCoalescingRewritten;       // ?? rewrite count
    public int NullCoalescingAssignRewritten; // ??= rewrite count
    public int TempVariablesGenerated;        // temp variables generated
    public int NestedRewrites;                // nested rewrite count
    public int SkippedUnsupported;            // unsupported scenario skip count
    public int ExpressionBodiesExpanded;      // '=>' bodies converted to blocks so guards have a statement slot
    public int ResidualCount;                 // post-self-check residual count

    /// <summary>
    /// W-06 (upgrade plan §4.3/G-18): residual ??/??= sites whose subtree
    /// carries a collection expression — owned downstream by
    /// expression-desugar (C36) and the converter's coalesce path — so they
    /// are deferred with a Warning instead of aborting the chain as a strict
    /// Error before the later passes ever run.
    /// </summary>
    public int DeferredDownstreamSites;
}
