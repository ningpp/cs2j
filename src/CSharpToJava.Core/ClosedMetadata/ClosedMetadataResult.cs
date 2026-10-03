namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>Result of rewriting one file (M3 rewriter product).</summary>
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
/// bootstrap <c>_Cs2jClosedTypes.&lt;ProjectName&gt;.cs</c> (M3, decision D6).
/// </summary>
public sealed record ClosedMetadataGeneratedFile(string ProjectName, string FileName, string Content);

/// <summary>
/// Whole-tree analysis product (design §5.3 phase 1). M2' skeleton: the
/// closed-type record set is empty — the CM-ENUM-1..4 collector lands in M3 and
/// will extend this record with the deterministic, key-sorted registration plan.
/// </summary>
public sealed record ClosedMetadataAnalysis
{
    public static ClosedMetadataAnalysis Empty { get; } = new();

    public IReadOnlyList<ClosedMetadataDiagnostic> Diagnostics { get; init; } =
        Array.Empty<ClosedMetadataDiagnostic>();
}
