namespace CSharpToJava.Core.EnumPreprocessor;

public sealed class EnumPreprocessorStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int BitwiseOrRewritten;
    public int BitwiseAndRewritten;
    public int BitwiseXorRewritten;
    public int BitwiseNotRewritten;
    public int CompoundAssignRewritten;
    public int HasFlagRewritten;
    public int NullableLiftedRewritten;
    public int SkippedUnsupported;
    public int ResidualCount;
}
