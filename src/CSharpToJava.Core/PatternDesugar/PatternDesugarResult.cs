namespace CSharpToJava.Core.PatternDesugar;

public sealed record PatternDesugarResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<PatternDesugarDiagnostic> Diagnostics,
    PatternDesugarStatistics Statistics);
