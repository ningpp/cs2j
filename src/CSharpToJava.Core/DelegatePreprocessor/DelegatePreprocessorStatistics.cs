namespace CSharpToJava.Core.DelegatePreprocessor;

public sealed class DelegatePreprocessorStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int CombineRewritten;          // d1 + d2 → Combine(d1, d2)
    public int RemoveRewritten;           // d1 - d2 → Remove(d1, d2)
    public int CompoundAssignRewritten;   // d += h / d -= h
    public int AnonymousMethodRewritten;  // delegate(...) {...} → lambda
    public int SkippedUnsupported;        // unsupported scenario skip count
    public int ResidualCount;             // post-self-check residual count
}
