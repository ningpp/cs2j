namespace CSharpToJava.Core.EnumPreprocessor;

public sealed record EnumPreprocessorResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<EnumPreprocessorDiagnostic> Diagnostics,
    EnumPreprocessorStatistics Statistics);
