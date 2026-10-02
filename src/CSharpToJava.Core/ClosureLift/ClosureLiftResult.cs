namespace CSharpToJava.Core.ClosureLift;

public sealed record ClosureLiftResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ClosureLiftDiagnostic> Diagnostics,
    ClosureLiftStatistics Statistics);
