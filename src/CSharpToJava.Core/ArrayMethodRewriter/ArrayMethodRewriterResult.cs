namespace CSharpToJava.Core.ArrayMethodRewriter;

public sealed record ArrayMethodRewriterResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ArrayMethodRewriterDiagnostic> Diagnostics,
    ArrayMethodRewriterStatistics Statistics);
