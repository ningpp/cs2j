namespace CSharpToJava.Core.UnsignedTypeRewriter;

public sealed record UnsignedTypeRewriterResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<UnsignedTypeRewriterDiagnostic> Diagnostics,
    UnsignedTypeRewriterStatistics Statistics);
