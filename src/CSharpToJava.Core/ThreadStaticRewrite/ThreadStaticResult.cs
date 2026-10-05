namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>Result of rewriting one file.</summary>
public sealed record ThreadStaticFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ThreadStaticDiagnostic> Diagnostics,
    ThreadStaticStatistics Statistics);
