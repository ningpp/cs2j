namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>Result of rewriting one file (Changed=false → runner byte-copies the source, BOM preserved).</summary>
public sealed record ExtensionFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ExtensionDiagnostic> Diagnostics,
    ExtensionMethodResolveStatistics Statistics)
{
    public static ExtensionFileResult Unchanged(string sourceCode) =>
        new(sourceCode, false, Array.Empty<ExtensionDiagnostic>(), new ExtensionMethodResolveStatistics());
}
