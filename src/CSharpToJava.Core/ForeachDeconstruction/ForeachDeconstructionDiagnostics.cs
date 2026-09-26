namespace CSharpToJava.Core.ForeachDeconstruction;

public enum ForeachDeconstructionSeverity { Info, Warning, Error }

public sealed record ForeachDeconstructionDiagnostic(
    ForeachDeconstructionSeverity Severity,
    string Message,
    string? FilePath = null,
    int? Line = null);

public sealed class ForeachDeconstructionStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int ForeachDeconstructionsLowered;
    public int ForeachDeconstructionsSkipped;
}

public sealed record ForeachDeconstructionResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ForeachDeconstructionDiagnostic> Diagnostics,
    ForeachDeconstructionStatistics Statistics);
