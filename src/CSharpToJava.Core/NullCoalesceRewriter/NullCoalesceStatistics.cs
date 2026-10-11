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
}
