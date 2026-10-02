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

    // ── C40 counters (design §5.3; M1 owns the FA buckets, FR fields are reserved for M3) ──

    /// <summary>FA-1/2/3: duck-typed enumerator sites lowered to while(MoveNext()) skeletons (CS2J5450).</summary>
    public int DuckEnumeratorSitesLowered;

    /// <summary>FA-2: lowered sites whose GetEnumerator is an extension method (CS2J5451).</summary>
    public int ExtensionGetEnumeratorSites;

    /// <summary>FA-3: sites iterating an enumerator variable directly, no GetEnumerator call (CS2J5452).</summary>
    public int DirectEnumeratorSites;

    /// <summary>Lowered sites whose enumerator is a value type (var keeps it concrete, no boxing; CS2J5453).</summary>
    public int StructEnumeratorSites;

    /// <summary>FA-4: sites with a pattern/IDisposable Dispose call generated in finally (CS2J5454).</summary>
    public int DisposePatternCallSites;

    /// <summary>Sites where explicit-interface-only Dispose forces a value-type enumerator to box (CS2J5455 bucket; M2 diagnostics).</summary>
    public int DisposeBoxedStructSites;

    /// <summary>Lowered duck-enumerator sites without any Dispose method (no try/finally generated).</summary>
    public int NoDisposeSites;

    /// <summary>FA-5: Span/ReadOnlySpan plain foreach lowered to index loops (CS2J5460).</summary>
    public int SpanIndexLoopSites;

    /// <summary>FR-1/2: ref foreach sites lowered to index loops (CS2J5480 bucket; M3).</summary>
    public int RefIndexLoopSites;

    /// <summary>FR: ref-readonly foreach sites (CS2J5481; M3).</summary>
    public int RefReadonlySites;

    /// <summary>FR-3: ref/ref-readonly Current enumerator sites (CS2J5482; M3).</summary>
    public int RefCurrentEnumeratorSites;

    /// <summary>dynamic-dispatched foreach sites left untouched with CS2J5456.</summary>
    public int DynamicForeachSites;

    /// <summary>Non-IEnumerable foreach sites whose binding is too incomplete to lower (CS2J5457; M2).</summary>
    public int UnresolvedForeachSites;

    /// <summary>FS-5 residual: bare non-array/non-string/non-IEnumerable plain foreach with a valid binding (CS2J5470; M2 gate).</summary>
    public int ResidualEnumeratorSites;

    /// <summary>FS-5 residual: foreach sites carrying RefKind != None (CS2J5495; M3 gate).</summary>
    public int ResidualRefSites;

    /// <summary>Diagnostics grouped by code (errors and warnings both recorded for sdk bucket reports).</summary>
    public Dictionary<string, int> ErrorsByCode { get; } = new(StringComparer.Ordinal);
}

public sealed record ForeachSourceResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ForeachSourceDiagnostic> Diagnostics,
    ForeachSourceStatistics Statistics);
