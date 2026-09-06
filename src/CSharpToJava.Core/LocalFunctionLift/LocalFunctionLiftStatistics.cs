namespace CSharpToJava.Core.LocalFunctionLift;

public sealed class LocalFunctionLiftStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int LocalFunctionsLifted;
    public int LocalFunctionsSkipped;
    public int LocalFunctionsResidual;
    public int CapturesByValue;
    public int CapturesByRef;
    public Dictionary<LocalFunctionSkipReason, int> SkippedByReason = new();
}
