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

/// <summary>Result of the post-rewrite residual gate (spec §4.5): per-rule counts plus gate diagnostics.</summary>
public sealed record GenericDeepenResidualReport(
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<GenericDeepenDiagnostic> Diagnostics)
{
    public static GenericDeepenResidualReport Empty { get; } =
        new(new Dictionary<string, int>(StringComparer.Ordinal), Array.Empty<GenericDeepenDiagnostic>());

    public int Total => Counts.Values.Sum();
}
