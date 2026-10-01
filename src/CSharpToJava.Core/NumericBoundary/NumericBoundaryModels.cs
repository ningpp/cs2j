using CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;
using CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// Per-file options for the numeric-boundary pass (C15). M1 ships no override
/// JSON: Error codes cannot be skipped/forced (design §4.6).
/// </summary>
public sealed record NumericBoundaryOptions
{
    /// <summary>Chain id of member-desugar (C14); checked user-operator shells are exempt from NB-13 only when it co-runs.</summary>
    public const string MemberDesugarPassId = "member-desugar";

    /// <summary>Upgrade residual-gate warnings (CS2J5327) to errors (same discipline as enum).</summary>
    public bool Strict { get; init; } = true;

    /// <summary>Files under released cs2j support-library trees are clean-copied by the adapter.</summary>
    public bool SkipSupportLibraryProjects { get; init; } = true;

    /// <summary>
    /// Project directory → <c>CheckForOverflowUnderflow</c> value, parsed per csproj
    /// by the adapter (design decision 15). The per-file default context is seeded
    /// from the owning project. M2 fully supports <c>true</c> projects.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? CheckForOverflowUnderflowByProjectDirectory { get; init; }

    /// <summary>Absolute source path of the file being rewritten (project-ownership lookup).</summary>
    public string? SourceFilePath { get; init; }

    /// <summary>
    /// Ids of the passes enabled in the current chain (Q6). A surviving checked
    /// user-operator shell is exempt from NB-13 only while member-desugar (C14)
    /// co-runs and strips it downstream.
    /// </summary>
    public IReadOnlySet<string>? EnabledPassIds { get; init; }

    /// <summary>
    /// C34: reduce built-in <see cref="decimal"/> operator sites to explicit
    /// BCL calls. Ships dormant (<c>false</c>): while false the decimal pipeline
    /// never runs and leaves zero trace; flips to default-on after M3.
    /// </summary>
    public bool EnableDecimalOps { get; init; }

    /// <summary>
    /// C17: normalize complex <c>++/--</c> targets (property/indexer/enum/
    /// decimal/unsigned-residual/nint/custom-operator) to explicit read-modify-
    /// write statements. Ships as a family rule kill-switch that is on by
    /// default together with the pass (no CLI toggle, design §5/开放问题 4);
    /// M0 only runs the read-only classifier and never rewrites.
    /// </summary>
    public bool EnableIncrementTargets { get; init; } = true;
}

public enum NumericBoundarySeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One diagnostic emitted by the numeric-boundary pass. Line/Column are 1-based
/// when a concrete syntax site is known (0 = tree/project-level diagnostic);
/// the adapter renders MSBuild-style <c>path(line,column): severity code: message</c>.
/// </summary>
public sealed record NumericBoundaryDiagnostic(
    NumericBoundarySeverity Severity,
    string Code,
    string Message,
    int Line = 0,
    int Column = 0);

/// <summary>
/// Per-file counters. The five generic counters follow the IPassStatistics
/// contract; the C15 buckets are defined in design §4.6. M1 only populates the
/// stripping/materialization/user-op buckets — checked helper counters stay 0
/// until M2/M3.
/// </summary>
public sealed class NumericBoundaryStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;

    // ── checked site buckets (M2/M3 fill these; 0 in M1) ──
    public int CheckedBinarySites;
    public int CheckedUnarySites;
    public int CheckedConversionSites;
    public int CheckedIncrementSites;
    public int CompoundAssignments;

    // ── M1 stripping buckets ──
    public int UncheckedExpressionsStripped;
    public int UncheckedBlocksUnwrapped;
    public int ConstantsMaterialized;

    /// <summary>
    /// <c>checked(e)</c> shells wrapping a non-integer (decimal/float/double/enum
    /// conversion/…) operation that M1 strips without touching the operation (NB-9/11).
    /// </summary>
    public int CheckedNonIntegerShellsStripped;

    /// <summary>
    /// <c>checked(e)</c> shells kept intact for member-desugar (C14): the inner
    /// operation binds to a user-defined <c>op_Checked*</c> variant. These are the
    /// NB-13 exemption vouchers (design decision 4).
    /// </summary>
    public int CheckedUserOpShellsDeferred;

    /// <summary>Projects with CheckForOverflowUnderflow=true observed for this file (M1: 0/1).</summary>
    public int CheckedOverflowProjectsDetected;

    /// <summary>Unchecked keyword nodes surviving in the output tree (must be 0; NB-13).</summary>
    public int ResidualUncheckedNodes;

    /// <summary>Checked nodes surviving in the output tree (allowed in M1; M2/M3 closes them).</summary>
    public int ResidualCheckedNodes;

    /// <summary>Checked helper calls emitted, grouped by integer domain (always empty in M1).</summary>
    public Dictionary<string, int> HelpersByDomain { get; } = new(StringComparer.Ordinal);

    /// <summary>Error diagnostics grouped by code.</summary>
    public Dictionary<string, int> ErrorsByReason { get; } = new(StringComparer.Ordinal);

    // ── C34 decimal-ops buckets (all zero while EnableDecimalOps=false) ──

    public DecimalOpStatistics DecimalOps { get; } = new();

    // ── C17 increment-target buckets (M0: classifier only; all zero) ──

    public IncrementTargetStatistics IncrementTargets { get; } = new();
}

public sealed record NumericBoundaryFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<NumericBoundaryDiagnostic> Diagnostics,
    NumericBoundaryStatistics Statistics);
