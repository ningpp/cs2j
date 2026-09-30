namespace CSharpToJava.Core.Preprocessing;

/// <summary>
/// Shared diagnostic codes emitted by the unified preprocess infrastructure.
/// </summary>
public static class PreprocessDiagnosticCodes
{
    /// <summary>
    /// A pass registered as a <see cref="PassStatus.Stub"/> placeholder was explicitly
    /// enabled. Fails loudly instead of silently producing unprocessed output.
    /// </summary>
    public const string StubNotImplemented = "CS2J9001";

    /// <summary>Unknown pass id supplied via <c>--enable-pass</c> / <c>--disable-pass</c> / <c>--pass</c>.</summary>
    public const string UnknownPass = "CS2J9002";

    // ── CS2J53xx numeric-boundary family (C15 checked/unchecked, design §4.5) ──
    // CS2J5300-5309: family-shared (restore/build/test/single-file gates).
    // CS2J5310-5329: C15 checked/unchecked.
    // CS2J5330-5349: C17 reserved; CS2J5350-5359: C22 reserved;
    // CS2J5360-5379: C34 reserved; CS2J5380-5399: C41 reserved (incl. sizeof).

    /// <summary>numeric-boundary: intermediate-tree <c>dotnet restore</c> failed.</summary>
    public const string NumericBoundaryRestoreFailed = "CS2J5302";

    /// <summary>numeric-boundary: intermediate-tree <c>dotnet build</c> failed.</summary>
    public const string NumericBoundaryBuildFailed = "CS2J5303";

    /// <summary>numeric-boundary: intermediate-tree <c>dotnet test</c> failed.</summary>
    public const string NumericBoundaryTestsFailed = "CS2J5304";

    /// <summary>numeric-boundary: single-file mode hit a checked helper site (needs the support library).</summary>
    public const string NumericBoundarySingleFileNotSelfContained = "CS2J5309";

    /// <summary>numeric-boundary: checked site explicitized (verbose-only aggregated info).</summary>
    public const string NumericBoundaryCheckedSiteInfo = "CS2J5310";

    /// <summary>numeric-boundary: unchecked expression/block stripped (verbose-only aggregated info).</summary>
    public const string NumericBoundaryUncheckedStrippedInfo = "CS2J5311";

    /// <summary>numeric-boundary: compile-time constant materialized as a literal (verbose-only info).</summary>
    public const string NumericBoundaryConstantMaterializedInfo = "CS2J5312";

    // CS2J5320: checked operation/conversion on unsupported numeric kinds (C41).
    // CS2J5321: checked floating-point/decimal → integer conversion.
    // CS2J5322: dynamically bound checked site.
    // CS2J5323: checked site inside an expression-tree lambda.
    // CS2J5324: complex ++ --/compound targets in checked context (C17).
    // CS2J5325: nullable lifted checked arithmetic (C22).

    /// <summary>numeric-boundary: project enables <c>&lt;CheckForOverflowUnderflow&gt;true&lt;/&gt;</c> (M1 placeholder).</summary>
    public const string NumericBoundaryProjectCheckedOverflow = "CS2J5326";

    /// <summary>numeric-boundary: residual unchecked/checked node survived in user-scope output (NB-13).</summary>
    public const string NumericBoundaryResidualNode = "CS2J5327";
}
