namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Result of rewriting one file.</summary>
public sealed record GenericDeepenFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<GenericDeepenDiagnostic> Diagnostics,
    GenericDeepenStatistics Statistics)
{
    public static GenericDeepenFileResult Unchanged(string sourceCode) =>
        new(sourceCode, false, Array.Empty<GenericDeepenDiagnostic>(), new GenericDeepenStatistics());
}
