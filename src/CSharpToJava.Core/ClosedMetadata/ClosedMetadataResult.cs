namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>Result of rewriting one file (M4 rewriter product).</summary>
public sealed record ClosedMetadataFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ClosedMetadataDiagnostic> Diagnostics,
    ClosedMetadataStatistics Statistics)
{
    public static ClosedMetadataFileResult Unchanged(string sourceCode) =>
        new(sourceCode, false, Array.Empty<ClosedMetadataDiagnostic>(), new ClosedMetadataStatistics());
}

/// <summary>
/// A file the pass adds to a project's compilation: the per-project registration
/// bootstrap <c>_Cs2jClosedTypes.&lt;ProjectName&gt;.cs</c> (decision D6).
/// </summary>
public sealed record ClosedMetadataGeneratedFile(string ProjectName, string FileName, string Content);

/// <summary>
/// Whole-tree analysis product (design §5.3 phase 1): the deduplicated, key-sorted
/// closed-type record set the registration generator consumes, with the collector's
/// diagnostics and statistics.
/// </summary>
public sealed record ClosedMetadataAnalysis
{
    public static ClosedMetadataAnalysis Empty { get; } = new();

    public IReadOnlyList<ClosedMetadataDiagnostic> Diagnostics { get; init; } =
        Array.Empty<ClosedMetadataDiagnostic>();

    public IReadOnlyList<ClosedTypeRecord> ClosedTypes { get; init; } =
        Array.Empty<ClosedTypeRecord>();

    public ClosedMetadataStatistics Statistics { get; init; } = new();
}

/// <summary>
/// Minimal M3 residual gate product (design §5.4 rules 2/5 only — duplicate/conflicting
/// normalized keys and duplicate registration statements inside generated files; the
/// remaining rules land with the M4 gate).
/// </summary>
public sealed record ClosedMetadataResidualResult(
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<ClosedMetadataDiagnostic> Diagnostics);
