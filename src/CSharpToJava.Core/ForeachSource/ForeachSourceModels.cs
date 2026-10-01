namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// Per-file options for the foreach-source pass (C16). v1 ships no override
/// JSON: residual rules cannot be skipped (design §4.5).
/// </summary>
public sealed class ForeachSourceOptions
{
    /// <summary>Upgrade Warning-level residual diagnostics/gate failures to errors (design §6).</summary>
    public bool Strict { get; init; }

    /// <summary>Whether to emit Info-level diagnostics (wrapped/ref/await counters).</summary>
    public bool ReportInfo { get; init; } = true;
}

public enum ForeachSourceSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One diagnostic emitted by the foreach-source pass. Line/Column are 1-based
/// when a concrete syntax site is known (null/0 = tree/project-level
/// diagnostic); the adapter renders MSBuild-style
/// <c>path(line,column): severity code: message</c>.
/// </summary>
public sealed record ForeachSourceDiagnostic(
    ForeachSourceSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null,
    int? Column = null);

/// <summary>
/// Per-file counters. The first three counters follow the shared pass
/// statistics contract; the C16 buckets are defined in design §4.5.
/// </summary>
public sealed class ForeachSourceStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;

    /// <summary>FS-1: plain foreach over string wrapped with ToCharArray() (incl. explicit-element temp inserts).</summary>
    public int StringForeachWrapped;

    /// <summary>FS-2: tuple-family residual deconstruction sites (long-term default: CS2J5420 error, no lowering).</summary>
    public int TupleResidualSeen;

    /// <summary>FS-3a: other-family residual deconstruction sites diagnosed (extension Deconstruct, …).</summary>
    public int OtherResidualDiagnosed;

    /// <summary>FS-3b: KeyValuePair deconstruction residuals (ordering anomaly; E07 should have lowered them).</summary>
    public int KvResidualSeen;

    /// <summary>Ruling 1: instance-Deconstruct residuals lowered into __e.Deconstruct(out …) forms.</summary>
    public int DeconstructResidualLowered;

    /// <summary>FS-4: foreach nodes carrying the ref/ref-readonly keyword (C40 surface; counted, never failed).</summary>
    public int RefForeachSeen;

    /// <summary>FS-4: await foreach nodes (C38 surface; counted, never failed).</summary>
    public int AwaitForeachSeen;

    /// <summary>Sites left untouched because they are out of this pass' scope (non-string sources, no model, error/dynamic types).</summary>
    public int SitesPassedThrough;

    /// <summary>FS-5 input: bare string-foreach sites that survive in this file's output (must be 0 when a model is available).</summary>
    public int ResidualStringSites;

    /// <summary>FS-5 input: tuple/other-family deconstruction sites that survive in this file's output.</summary>
    public int ResidualDeconstructionSites;

    /// <summary>Diagnostics grouped by code (errors and warnings both recorded for sdk bucket reports).</summary>
    public Dictionary<string, int> ErrorsByCode { get; } = new(StringComparer.Ordinal);
}

public sealed record ForeachSourceResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ForeachSourceDiagnostic> Diagnostics,
    ForeachSourceStatistics Statistics);
