namespace CSharpToJava.Core.LocalFunctionLift;

public sealed record LocalFunctionLiftResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<LocalFunctionLiftDiagnostic> Diagnostics,
    LocalFunctionLiftStatistics Statistics);
