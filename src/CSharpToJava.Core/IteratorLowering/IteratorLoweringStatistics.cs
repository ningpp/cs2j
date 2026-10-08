namespace CSharpToJava.Core.IteratorLowering;

public sealed class IteratorLoweringStatistics
{
    // Common fields
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;

    // Pass-specific fields
    public int IteratorMembersLowered;
    public int StateMachinesGenerated;
    public int YieldReturnSites;
    public int YieldBreakSites;
    public int HoistedParameters;
    public int HoistedLocals;
    public int HoistedEnumerators;
    public int FinallyRegions;
    public int SkippedUnsupported;
}
