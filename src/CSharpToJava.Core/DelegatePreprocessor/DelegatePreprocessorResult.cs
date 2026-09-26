namespace CSharpToJava.Core.DelegatePreprocessor;

public sealed record DelegatePreprocessorResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<DelegatePreprocessorDiagnostic> Diagnostics,
    DelegatePreprocessorStatistics Statistics);
