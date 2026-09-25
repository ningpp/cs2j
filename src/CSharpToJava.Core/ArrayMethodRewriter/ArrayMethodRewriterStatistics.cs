namespace CSharpToJava.Core.ArrayMethodRewriter;

public sealed class ArrayMethodRewriterStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int InstanceMemberRewrites;
    public int StaticArrayCalls;
    public int ResizeStatements;
    public int ForeachFlattened;
    public int RankInlined;
    public int MultiDimLengthRewrites;
}
