namespace CSharpToJava.Core.NullCoalesceRewriter;

public sealed record NullCoalesceResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<NullCoalesceDiagnostic> Diagnostics,
    NullCoalesceStatistics Statistics);
