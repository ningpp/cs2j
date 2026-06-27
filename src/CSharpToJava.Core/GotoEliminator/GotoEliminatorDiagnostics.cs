namespace CSharpToJava.Core.GotoEliminator;

public enum GotoEliminatorSeverity { Warning, Error }

public sealed record GotoEliminatorDiagnostic(
    GotoEliminatorSeverity Severity,
    string Message,
    string? MethodName = null,
    int? Line = null);

public sealed class GotoEliminatorStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int MethodsTransformed;
    public int MethodsSkipped;
    public int GotosEliminated;
}

public sealed record GotoEliminatorResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<GotoEliminatorDiagnostic> Diagnostics,
    GotoEliminatorStatistics Statistics);

public sealed class GotoEliminatorException : Exception
{
    public GotoEliminatorException(string message) : base(message) { }
}
