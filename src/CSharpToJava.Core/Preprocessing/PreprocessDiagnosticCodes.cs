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

    /// <summary>numeric-boundary: checked operation/conversion on unsupported numeric kinds (Half/Int128/nint/…, C41).</summary>
    public const string NumericBoundaryUnsupportedType = "CS2J5320";

    /// <summary>numeric-boundary: checked floating-point/decimal → integer conversion.</summary>
    public const string NumericBoundaryFloatingToInteger = "CS2J5321";

    /// <summary>numeric-boundary: dynamically bound checked site.</summary>
    public const string NumericBoundaryDynamic = "CS2J5322";

    /// <summary>numeric-boundary: checked site inside an expression-tree lambda.</summary>
    public const string NumericBoundaryExpressionTree = "CS2J5323";

    /// <summary>numeric-boundary: complex ++/--/compound target in checked context (C17).</summary>
    public const string NumericBoundaryComplexIncrement = "CS2J5324";

    /// <summary>numeric-boundary: nullable lifted checked arithmetic (C22).</summary>
    public const string NumericBoundaryNullableLifted = "CS2J5325";

    // CS2J5326 was the M1 placeholder for project-wide CheckForOverflowUnderflow;
    // M2 supports that setting fully, so the code is retired (never emitted).
    // public const string NumericBoundaryProjectCheckedOverflow = "CS2J5326";

    /// <summary>numeric-boundary: residual unchecked/checked node survived in user-scope output (NB-13).</summary>
    public const string NumericBoundaryResidualNode = "CS2J5327";

    // ── CS2J5360-5379: C34 decimal-ops normalization (design §6) ──

    /// <summary>C34: decimal operator site reduction count (verbose-only aggregated info).</summary>
    public const string DecimalOpsSiteInfo = "CS2J5360";

    /// <summary>C34: operand type cannot be classified/promoted safely (non-integer domain, unknown wrapper).</summary>
    public const string DecimalOpsUnsupportedOperand = "CS2J5361";

    /// <summary>C34: dynamically bound decimal operation.</summary>
    public const string DecimalOpsDynamic = "CS2J5362";

    /// <summary>C34: decimal operation inside an expression-tree lambda (Expression&lt;TDelegate&gt;).</summary>
    public const string DecimalOpsExpressionTree = "CS2J5363";

    // CS2J5364 reserved: Nullable<decimal> lifted sites belong to C22; they are
    // skipped silently and never diagnosed on the normal path.

    /// <summary>C34: complex-target decimal compound assignment with no safe hoist anchor.</summary>
    public const string DecimalOpsNoAnchor = "CS2J5365";

    /// <summary>C34: ToDecimalUInt32/64 emitted but the E09 support surface is unavailable.</summary>
    public const string DecimalOpsUnsignedSupportMissing = "CS2J5366";

    /// <summary>C34: DC-13 residual gate — a built-in decimal operator survived in user output.</summary>
    public const string DecimalOpsResidualOperator = "CS2J5367";

    /// <summary>C34: const decimal downgraded to static readonly / plain local (verbose-only info).</summary>
    public const string DecimalOpsConstDowngradedInfo = "CS2J5368";

    // CS2J5369 reserved: compat method-surface verification (CI test level).
    // CS2J5370-5379 reserved.

    // ── CS2J54xx foreach-source family (C16 string foreach / residual deconstruction) ──
    // CS2J5400-5409: family-shared (restore/build/test gates).
    // CS2J5410-5419: string source (FS-1).
    // CS2J5420-5439: residual deconstruction (FS-2/FS-3).
    // CS2J5440-5449: residual gate / ref / await counters (FS-4/FS-5).
    // CS2J5450-5499: reserved for C40 (EnumeratorAdapter / RefForeach).

    /// <summary>foreach-source: intermediate-tree <c>dotnet restore</c> failed.</summary>
    public const string ForeachSourceRestoreFailed = "CS2J5402";

    /// <summary>foreach-source: intermediate-tree <c>dotnet build</c> failed.</summary>
    public const string ForeachSourceBuildFailed = "CS2J5403";

    /// <summary>foreach-source: intermediate-tree <c>dotnet test</c> failed.</summary>
    public const string ForeachSourceTestsFailed = "CS2J5404";

    /// <summary>foreach-source: string foreach wrapped with ToCharArray() (verbose-only aggregated info).</summary>
    public const string ForeachSourceStringWrapped = "CS2J5410";

    /// <summary>foreach-source: tuple-family deconstruction residual reached this pass (E06 should have handled it).</summary>
    public const string ForeachSourceTupleResidual = "CS2J5420";

    /// <summary>foreach-source: other-family deconstruction residual cannot be normalized (extension Deconstruct, …).</summary>
    public const string ForeachSourceOtherResidual = "CS2J5421";

    /// <summary>foreach-source: KeyValuePair deconstruction residual (foreach-deconstruction should have handled it).</summary>
    public const string ForeachSourceKvResidual = "CS2J5422";

    /// <summary>foreach-source: residual gate found bare string foreach or tuple/other deconstruction sites in the output tree.</summary>
    public const string ForeachSourceResidualGate = "CS2J5440";

    /// <summary>foreach-source: ref/ref readonly foreach site left untouched (belongs to C40; verbose-only info).</summary>
    public const string ForeachSourceRefForeach = "CS2J5441";

    /// <summary>foreach-source: await foreach site left untouched (belongs to C38; verbose-only info).</summary>
    public const string ForeachSourceAwaitForeach = "CS2J5442";
}
