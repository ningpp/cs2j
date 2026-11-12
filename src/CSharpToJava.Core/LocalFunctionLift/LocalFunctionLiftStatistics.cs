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
    public int CapturesByHolder;
    /// <summary>Whole-file rollbacks triggered by the V-09 atomicity guard.</summary>
    public int FilesRolledBackAtomicity;
    /// <summary>Lift plans reverted by an atomicity rollback (a file may hold several).</summary>
    public int PlansRolledBackAtomicity;
    public Dictionary<LocalFunctionSkipReason, int> SkippedByReason = new();
}
