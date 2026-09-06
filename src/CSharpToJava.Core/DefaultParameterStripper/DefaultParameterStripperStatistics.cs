namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed class DefaultParameterStripperStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int DeclarationsStripped;
    public int ParametersStripped;
    public int OverloadsGenerated;
    public int SkippedNoBody;
    public int SkippedConflict;
    public int SkippedHidesAbstract;
    public int SkippedUnsupported;
}
