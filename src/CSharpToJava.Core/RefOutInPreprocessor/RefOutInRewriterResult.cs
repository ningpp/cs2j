namespace CSharpToJava.Core.RefOutInPreprocessor;

public sealed record RefOutInRewriterResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<RefOutInRewriterDiagnostic> Diagnostics,
    RefOutInRewriterStatistics Statistics);
