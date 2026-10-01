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

    // ── CS2J5330-5341: C17 complex increment/decrement normalization (design §6) ──

    /// <summary>C17: increment/decrement site expanded (verbose-only aggregated info, by target/core).</summary>
    public const string IncrementTargetSiteInfo = "CS2J5330";

    /// <summary>C17: read-modify-write temporary hoisted (verbose-only aggregated info, by role).</summary>
    public const string IncrementTargetTempInfo = "CS2J5331";

    /// <summary>C17: event/pointer/other illegal or unowned increment target (event → C13; pointer → C06; read-only property baseline-illegal).</summary>
    public const string IncrementTargetIllegalTarget = "CS2J5332";

    /// <summary>C17: nint/nuint/IntPtr/UIntPtr increment target — arithmetic core belongs to C41.</summary>
    public const string IncrementTargetNativeInt = "CS2J5333";

    /// <summary>
    /// C17: decimal increment expanded as <c>old ± 1m</c>; the Java half is owned
    /// by C34. C34 已合入，2026-10-01 裁决降 Info，不再经 Warning（开放问题 2 裁决更新）。
    /// </summary>
    public const string IncrementTargetDecimalInfo = "CS2J5334";

    /// <summary>C17: nullable numeric/enum lifted increment — belongs to C22.</summary>
    public const string IncrementTargetNullableLifted = "CS2J5335";

    /// <summary>C17: dynamically bound increment target (force cannot clear).</summary>
    public const string IncrementTargetDynamic = "CS2J5336";

    /// <summary>C17: increment inside an expression-tree lambda changes the tree shape (force cannot clear).</summary>
    public const string IncrementTargetExpressionTree = "CS2J5337";

    /// <summary>C17: user-defined op_Increment/op_Decrement site; C14 defers ++/-- and the naming contract is not frozen.</summary>
    public const string IncrementTargetCustomOperator = "CS2J5338";

    /// <summary>C17: value-consumed postfix / read-modify-write with no safe hoist anchor (condition/query/lambda/initializer/attribute/?.).</summary>
    public const string IncrementTargetNoAnchor = "CS2J5339";

    /// <summary>C17: ref-returning target expanded via ref alias; the Java half depends on C01 (strict → Error).</summary>
    public const string IncrementTargetRefReturn = "CS2J5340";

    /// <summary>C17: IT-13 residual gate — an owned ++/-- node survived in the output tree (strict → Error).</summary>
    public const string IncrementTargetResidual = "CS2J5341";

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
}
