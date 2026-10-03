using SampleNumericBoundaryLib;
using SampleNumericBoundaryLib.DecimalOps;
using SampleNumericBoundaryLib.IncrementTargets;
using SampleNumericBoundaryLib.Nullable;

var failures = 0;

void Check(string name, bool ok)
{
    if (!ok)
    {
        Console.Error.WriteLine($"FAIL: {name}");
        failures++;
    }
}

// ── stripped unchecked sites: wraparound behavior preserved ──
Check("WrapUInt32Max", UncheckedSamples.WrapUInt32ToInt32(0xFFFFFFFFu) == -1);
Check("WrapToByteOverflow", UncheckedSamples.WrapToByte(256) == 0);
Check("WrapToByteNegative", UncheckedSamples.WrapToByte(-1) == 255);
Check("WrapAddWraps", UncheckedSamples.WrapAdd(int.MaxValue, 1) == int.MinValue);
Check("UnwrapBlockWraps", UncheckedSamples.UnwrapBlock(int.MaxValue, 0) == int.MinValue);

// ── nested site: inner unchecked wins inside the outer checked block ──
Check("NestedInsideChecked", UncheckedSamples.NestedInsideChecked(40, 2) == 42);

// ── NB-9/11: decimal shell stripped, call intact ──
Check("CheckedDecimalAdd", UncheckedSamples.CheckedDecimalAdd(1m, 2m) == 3m);

// ── NB-6: materialized constant ──
Check("ConstantMaterialized", UncheckedSamples.U64MaxAsLong == -1L);

// ── M2/M3 checked helpers: non-overflowing inputs behave identically ──
Check("CheckedAddNonOverflow", CheckedSamples.CheckedAdd(40, 2) == 42);
Check("CheckedSubNonOverflow", CheckedSamples.CheckedSub(40, 2) == 38);
Check("CheckedMulNonOverflow", CheckedSamples.CheckedMul(40, 2) == 80);
Check("CheckedAdd64", CheckedSamples.CheckedAdd64(40L, 2L) == 42L);
Check("CheckedAddU32", CheckedSamples.CheckedAddU32(40u, 2u) == 42u);
Check("CheckedAddU64", CheckedSamples.CheckedAddU64(40ul, 2ul) == 42ul);
Check("CheckedNegate", CheckedSamples.CheckedNegate(42) == -42);
Check("CheckedNegateUInt", CheckedSamples.CheckedNegateUInt(1u) == -1L);
Check("CheckedNarrowNonOverflow", CheckedSamples.CheckedNarrow(200) == 200);
Check("CheckedNarrowFromU64", CheckedSamples.CheckedNarrowFromU64(42ul) == 42);
Check("CheckedBlockNonOverflow",
    CheckedSamples.CheckedBlockSum(new[] { 1, 2, 3 }) == 6);
Check("CheckedByteCompound", CheckedSamples.CheckedByteCompound(200, 50) == 250);
Check("CheckedPrefix", CheckedSamples.CheckedPrefix(41) == 42);
Check("CheckedPostfixReturned", CheckedSamples.CheckedPostfixReturned(42) == 42);
Check("CheckedEnumIncrement",
    CheckedSamples.CheckedEnumIncrement(CountKind.One) == CountKind.Two);

// ── overflowing inputs still throw at runtime through the helpers ──
var addThrew = false;
try
{
    CheckedSamples.CheckedAdd(int.MaxValue, 1);
}
catch (OverflowException)
{
    addThrew = true;
}

Check("CheckedAddThrows", addThrew);

var narrowThrew = false;
try
{
    CheckedSamples.CheckedNarrow(256);
}
catch (OverflowException)
{
    narrowThrew = true;
}

Check("CheckedNarrowThrows", narrowThrew);

var byteCompoundThrew = false;
try
{
    CheckedSamples.CheckedByteCompound(200, 100);
}
catch (OverflowException)
{
    byteCompoundThrew = true;
}

Check("CheckedByteCompoundThrows", byteCompoundThrew);

var blockThrew = false;
try
{
    CheckedSamples.CheckedBlockSum(new[] { int.MaxValue, 1 });
}
catch (OverflowException)
{
    blockThrew = true;
}

Check("CheckedBlockThrows", blockThrew);

// ── C34 decimal-ops: BCL calls preserve decimal arithmetic ──
Check("DecimalAdd", DecimalArithmetic.Add(1.25m, 2.5m) == 3.75m);
Check("DecimalSubtract", DecimalArithmetic.Subtract(10m, 4m) == 6m);
Check("DecimalMultiply", DecimalArithmetic.Multiply(3m, 2.5m) == 7.5m);
Check("DecimalDivide", DecimalArithmetic.Divide(10m, 4m) == 2.5m);
Check("DecimalRemainder", DecimalArithmetic.Remainder(10m, 3m) == 1m);
Check("DecimalNegate", DecimalArithmetic.Negate(42m) == -42m);
Check("DecimalPlusStripped", DecimalArithmetic.PlusStripped(42m) == 42m);
Check("DecimalChained", DecimalArithmetic.ChainedArithmetic(2m, 4m, 3m) == 17.5m);

// Equality normalizes scale through decimal.Compare.
Check("DecimalScaleEquality", DecimalArithmetic.DifferentScalesCompareEqual());
Check("DecimalEqualsOp", DecimalArithmetic.EqualsOp(1.0m, 1.00m));
Check("DecimalNotEqualsOp", !DecimalArithmetic.NotEqualsOp(1.0m, 1.00m));
Check("DecimalRelational", DecimalArithmetic.LessThan(1m, 2m)
    && DecimalArithmetic.LessThanOrEqual(1m, 1m)
    && DecimalArithmetic.GreaterThan(2m, 1m)
    && DecimalArithmetic.GreaterThanOrEqual(1m, 1m));

// Decimal mixed operands.
Check("DecimalMixedIntegers", DecimalMixedOperands.SignedAndNarrowIntegers(
    1m, 1, 1, 1, (char)1, 1L, 1, 1) == 8m);
Check("DecimalMixedLiterals", DecimalMixedOperands.IntegerLiteralsSymmetric(10m) == 44m);
Check("DecimalRealUInt32", DecimalMixedOperands.RealUInt32(1m, 42u) == 43m);
Check("DecimalRealUInt64", DecimalMixedOperands.RealUInt64(10m, 42ul) == 420m);
Check("DecimalMixedAccumulation",
    DecimalMixedOperands.MixedAccumulation(10m, 42u, 40) == 470m);

// DC-12 const downgrade keeps values.
Check("DecimalConstantsPure", DecimalConstants.Pure == 1.5m);
Check("DecimalConstantsLocalMix", DecimalConstants.LocalMix() == 25.75m);
Check("DecimalConstantsCascade",
    DecimalConstants.Seeded == 6m
    && DecimalConstants.CascadedFromSeeded == 6m
    && DecimalConstants.CascadedFromFactor == 3m);

// DC-11 single evaluation: getter/setter/receiver/argument each once.
var propertyProbe = new DecimalProbe();
DecimalCompoundSamples.PropertyCompound(propertyProbe, 5m);
Check("DecimalPropertyCompoundResult", propertyProbe.P == 5m);
// One getter for the compound read, one inside PropertyCompound's return,
// one for the result comparison above; the setter runs exactly once.
Check("DecimalPropertyGetterCount", propertyProbe.PropertyGets == 3);
Check("DecimalPropertySetterOnce", propertyProbe.PropertySets == 1);

var fieldProbe = new DecimalProbe();
DecimalCompoundSamples.FieldCompound(fieldProbe, 3m);
Check("DecimalFieldCompoundResult", fieldProbe.F == -3m);

var receiverProbe = new DecimalProbe();
receiverProbe.CompoundOnPropertyViaMethodReceiver(7m);
Check("DecimalReceiverOnce", receiverProbe.ReceiverEvaluations == 1);
Check("DecimalReceiverPropertySetterOnce", receiverProbe.PropertySets == 1);

var indexerProbe = new DecimalProbe();
DecimalCompoundSamples.IndexerCompound(indexerProbe, 6m);
Check("DecimalIndexArgOnce", indexerProbe.IndexArgEvaluations == 1);
Check("DecimalIndexGetterOnce", indexerProbe.IndexerGets == 1);
Check("DecimalIndexSetterOnce", indexerProbe.IndexerSets == 1);
Check("DecimalIndexValue", indexerProbe[1] == 0m);

// Decimal arithmetic overflow and division-by-zero stay runtime exceptions.
CheckThrows<OverflowException>("DecimalAddOverflow",
    () => DecimalArithmetic.Add(decimal.MaxValue, 1m));
CheckThrows<OverflowException>("DecimalMultiplyOverflow",
    () => DecimalArithmetic.Multiply(decimal.MaxValue, 2m));
CheckThrows<DivideByZeroException>("DecimalDivideByZero",
    () => DecimalArithmetic.Divide(1m, 0m));
CheckThrows<DivideByZeroException>("DecimalRemainderByZero",
    () => DecimalArithmetic.Remainder(1m, 0m));

// ── C17 M1 increment-targets: single-evaluation read-modify-write ──
var standaloneProbe = new SideEffectProbe();
Check("IncPropertyStandaloneResult", standaloneProbe.PropertyStandaloneResult() == 1);
Check("IncPropertyStandaloneCounters",
    standaloneProbe.IntPropGets == 1 && standaloneProbe.IntPropSets == 1);

var postfixProbe = new SideEffectProbe();
var (postfixOld, postfixNow) = postfixProbe.PostfixConsumedProperty();
Check("IncPostfixOldValue", postfixOld == 0 && postfixNow == 1);
Check("IncPostfixCounters",
    postfixProbe.IntPropGets == 1 && postfixProbe.IntPropSets == 1);

var prefixProbe = new SideEffectProbe();
Check("IncPrefixReturned", prefixProbe.PrefixReturnedProperty() == 1);
Check("IncPrefixCounters",
    prefixProbe.IntPropGets == 1 && prefixProbe.IntPropSets == 1);

var effectfulProbe = new SideEffectProbe();
Check("IncEffectfulReceiverResult", effectfulProbe.EffectfulReceiverProperty() == 1);
Check("IncEffectfulReceiverOnce",
    effectfulProbe.FactoryCalls == 1
    && effectfulProbe.IntPropGets == 1
    && effectfulProbe.IntPropSets == 1);

var prefixEffectfulProbe = new SideEffectProbe();
Check("IncPrefixEffectfulReceiverResult", prefixEffectfulProbe.PrefixEffectfulReceiver() == 1);
Check("IncPrefixEffectfulReceiverOnce", prefixEffectfulProbe.FactoryCalls == 1);

var incIndexerProbe = new SideEffectProbe();
incIndexerProbe.IndexerIncrementOnce();
Check("IncIndexerArgOnce", incIndexerProbe.NextCalls == 1);
Check("IncIndexerAccessOnce",
    incIndexerProbe.IndexerGets == 1
    && incIndexerProbe.IndexerSets == 1
    && incIndexerProbe.IndexArgEvaluations == 2);
Check("IncIndexerStoredValue", incIndexerProbe.IndexStoredValue(1) == 1);

var uintProbe = new SideEffectProbe();
Check("IncUIntProperty", uintProbe.UIntPropertyIncrement() == 1u);

var byteProbe = new SideEffectProbe();
Check("IncByteProperty", byteProbe.BytePropertyIncrement() == 1);
Check("IncBytePropertyCounters",
    byteProbe.BytePropGets == 1 && byteProbe.BytePropSets == 1);

var checkedColorProbe = new SideEffectProbe();
Check("IncCheckedColorProperty",
    checkedColorProbe.CheckedColorPropertyIncrement() == IncrementColor.Green);
Check("IncCheckedColorCounters",
    checkedColorProbe.ColorPropGets == 1 && checkedColorProbe.ColorPropSets == 1);

Check("IncSimpleEnumLocal", EnumIncrementSamples.SimpleEnumLocal() == IncrementRank.Two);
Check("IncByteEnumWraps", EnumIncrementSamples.ByteEnumWraps() == IncrementColor.Red);

var enumArray = new[] { IncrementColor.Red, IncrementColor.Green };
Check("IncEnumArrayPostfixOld",
    EnumIncrementSamples.EnumArrayElementPostfix(enumArray, 1) == IncrementColor.Green);
Check("IncEnumArrayPostfixNew", enumArray[1] == IncrementColor.Blue);

var enumArrayPrefix = new[] { IncrementColor.Red, IncrementColor.Green };
Check("IncEnumArrayPrefix",
    EnumIncrementSamples.EnumArrayElementPrefix(enumArrayPrefix, 0) == IncrementColor.Green);

Check("IncUIntResidualRoundTrip", EnumIncrementSamples.UIntResidualRoundTrip(42u) == 42u);
Check("IncULongResidualIncrement", EnumIncrementSamples.ULongResidualIncrement(42ul) == 43ul);
Check("IncByteResidualIncrement", EnumIncrementSamples.ByteResidualIncrement(200) == 201);
Check("IncUShortResidualIncrement", EnumIncrementSamples.UShortResidualIncrement(200) == 201);
Check("IncByteResidualWraps", EnumIncrementSamples.ByteResidualWraps() == 0);
Check("IncUIntResidualWraps", EnumIncrementSamples.UIntResidualWraps() == 0u);

// ── C17 M2 IT-7: decimal ++/-- expanded to old ± 1m ──
var decimalProbe = new DecimalIncrementSamples();
Check("IncDecimalProperty", decimalProbe.DecimalPropertyIncrement() == 1m);
Check("IncDecimalPropertyCounters",
    decimalProbe.DecimalPropGets == 1 && decimalProbe.DecimalPropSets == 1);
Check("DecDecimalProperty", decimalProbe.DecimalPropertyDecrement() == 2m);
Check("DecDecimalPropertyCounters",
    decimalProbe.DecimalPropGets == 2 && decimalProbe.DecimalPropSets == 2);

decimalProbe.DecimalArrayIncrementOnce();
Check("IncDecimalArrayValue", decimalProbe.DecimalArrayStoredValue() == 1m);
Check("IncDecimalArrayIndexOnce", decimalProbe.DecimalIndexEvaluations == 1);

Check("IncDecimalPrecisionTiny", DecimalIncrementSamples.PrecisionTinySurvives());
Check("IncDecimalPrecisionIncrement", DecimalIncrementSamples.PrecisionIncrement() == 2m);

// ════════════════════════════════════════════════════════════════════
// C22 M2 nullable-lift: Native (compiler lifted truth) vs Guarded
// (hand-written explicit shape) agree on the full null/value grids.
// Comparisons go through EqualityComparer so this driver itself emits no
// lifted Nullable<T> operator sites.
// ════════════════════════════════════════════════════════════════════

void CheckPair<TResult>(string name, TResult native, TResult guarded) =>
    Check(name, EqualityComparer<TResult>.Default.Equals(native, guarded));

void CheckGrid1<TArg, TResult>(
    string name,
    Func<TArg?, TResult> native,
    Func<TArg?, TResult> guarded,
    TArg?[] values)
    where TArg : struct
{
    foreach (var a in values)
    {
        CheckPair($"{name}({GridLabel(a)})", native(a), guarded(a));
    }
}

void CheckGrid2<TArg, TResult>(
    string name,
    Func<TArg?, TArg?, TResult> native,
    Func<TArg?, TArg?, TResult> guarded,
    TArg?[] values)
    where TArg : struct
{
    foreach (var a in values)
    {
        foreach (var b in values)
        {
            CheckPair($"{name}({GridLabel(a)},{GridLabel(b)})", native(a, b), guarded(a, b));
        }
    }
}

static string GridLabel<T>(T? v) where T : struct =>
    v.HasValue ? v.Value.ToString() ?? "?" : "null";

// ── NL-1/NL-2 arithmetic and unary (boundary wraps in unchecked context) ──
CheckGrid2("NullableAdd",
    NullableArithmeticSamples.NativeAdd, NullableArithmeticSamples.GuardedAdd,
    new int?[] { null, int.MaxValue, 2 });
foreach (var a in new int?[] { null, 1, int.MaxValue })
{
    foreach (var b in new[] { 1, -2 })
    {
        CheckPair($"NullableAddOneSided({GridLabel(a)},{b})",
            NullableArithmeticSamples.NativeAddOneSided(a, b),
            NullableArithmeticSamples.GuardedAddOneSided(a, b));
    }
}

CheckGrid2("NullableSubtract",
    NullableArithmeticSamples.NativeSubtract, NullableArithmeticSamples.GuardedSubtract,
    new long?[] { null, long.MaxValue, 5L });
foreach (var a in new double?[] { null, 1.5, -2.0 })
{
    foreach (var b in new[] { 2.0, -3.0 })
    {
        CheckPair($"NullableMultiply({GridLabel(a)},{b})",
            NullableArithmeticSamples.NativeMultiply(a, b),
            NullableArithmeticSamples.GuardedMultiply(a, b));
    }
}

CheckGrid1("NullableNegate",
    NullableArithmeticSamples.NativeNegate, NullableArithmeticSamples.GuardedNegate,
    new int?[] { null, -7, 42 });
CheckGrid1("NullableUnaryPlus",
    NullableArithmeticSamples.NativeUnaryPlus, NullableArithmeticSamples.GuardedUnaryPlus,
    new long?[] { null, 42L, -9L });
CheckPair("NullableAddBoundaryWrap",
    NullableArithmeticSamples.NativeAdd(int.MaxValue, int.MaxValue),
    NullableArithmeticSamples.GuardedAdd(int.MaxValue, int.MaxValue));

// ── NL-3 relational: null>=null is false (pinned counter-intuitive cell) ──
var relationalValues = new int?[] { null, 1, 2 };
CheckGrid2("NullableGe",
    NullableRelationalSamples.NativeGreaterThanOrEqual,
    NullableRelationalSamples.GuardedGreaterThanOrEqual, relationalValues);
CheckGrid2("NullableGt",
    NullableRelationalSamples.NativeGreaterThan,
    NullableRelationalSamples.GuardedGreaterThan, relationalValues);
CheckGrid2("NullableLe",
    NullableRelationalSamples.NativeLessThanOrEqual,
    NullableRelationalSamples.GuardedLessThanOrEqual, relationalValues);
CheckGrid2("NullableLt",
    NullableRelationalSamples.NativeLessThan,
    NullableRelationalSamples.GuardedLessThan, relationalValues);
CheckPair("NullableNullGeNull",
    NullableRelationalSamples.NativeGreaterThanOrEqual(null, null),
    NullableRelationalSamples.GuardedGreaterThanOrEqual(null, null));
foreach (var a in relationalValues)
{
    CheckPair($"NullableGeOneSided({GridLabel(a)})",
        NullableRelationalSamples.NativeGreaterThanOrEqualOneSided(a, 1),
        NullableRelationalSamples.GuardedGreaterThanOrEqualOneSided(a, 1));
    CheckPair($"NullableLtOneSided({GridLabel(a)})",
        NullableRelationalSamples.NativeLessThanOneSided(a, 1),
        NullableRelationalSamples.GuardedLessThanOneSided(a, 1));
}

// ── NL-4 equality nine-cell grids; double null == null is true ──
var equalityValues = new int?[] { null, 0, 1 };
CheckGrid2("NullableEq",
    NullableEqualitySamples.NativeEqual, NullableEqualitySamples.GuardedEqual,
    equalityValues);
CheckGrid2("NullableNe",
    NullableEqualitySamples.NativeNotEqual, NullableEqualitySamples.GuardedNotEqual,
    equalityValues);
CheckPair("NullableNullEqNull",
    NullableEqualitySamples.NativeEqual(null, null),
    NullableEqualitySamples.GuardedEqual(null, null));
foreach (var a in equalityValues)
{
    CheckPair($"NullableEqOneSided({GridLabel(a)})",
        NullableEqualitySamples.NativeEqualOneSided(a, 0),
        NullableEqualitySamples.GuardedEqualOneSided(a, 0));
    CheckPair($"NullableNeOneSided({GridLabel(a)})",
        NullableEqualitySamples.NativeNotEqualOneSided(a, 0),
        NullableEqualitySamples.GuardedNotEqualOneSided(a, 0));
}

CheckGrid1("NullableBoolEqTrue",
    NullableEqualitySamples.NativeBoolEqualTrue,
    NullableEqualitySamples.GuardedBoolEqualTrue,
    new bool?[] { null, false, true });
CheckGrid1("NullableIsNull",
    NullableEqualitySamples.NativeIsNull, NullableEqualitySamples.GuardedIsNull,
    equalityValues);
CheckGrid1("NullableIsNotNull",
    NullableEqualitySamples.NativeIsNotNull, NullableEqualitySamples.GuardedIsNotNull,
    equalityValues);

// ── NL-5 bool? three-valued logic full table (absorption cells pinned) ──
var boolValues = new bool?[] { null, false, true };
CheckGrid2("NullableBoolAnd",
    NullableBoolLogicSamples.NativeAnd, NullableBoolLogicSamples.GuardedAnd, boolValues);
CheckGrid2("NullableBoolOr",
    NullableBoolLogicSamples.NativeOr, NullableBoolLogicSamples.GuardedOr, boolValues);
CheckGrid2("NullableBoolXor",
    NullableBoolLogicSamples.NativeXor, NullableBoolLogicSamples.GuardedXor, boolValues);
CheckGrid1("NullableBoolNot",
    NullableBoolLogicSamples.NativeNot, NullableBoolLogicSamples.GuardedNot, boolValues);
CheckPair("NullableFalseAndNull",
    NullableBoolLogicSamples.NativeAnd(false, null),
    NullableBoolLogicSamples.GuardedAnd(false, null));
CheckPair("NullableTrueOrNull",
    NullableBoolLogicSamples.NativeOr(true, null),
    NullableBoolLogicSamples.GuardedOr(true, null));
CheckPair("NullableNullAndNull",
    NullableBoolLogicSamples.NativeAnd(null, null),
    NullableBoolLogicSamples.GuardedAnd(null, null));

// ── NL-6 GetValueOrDefault (default argument evaluates exactly once) ──
CheckGrid1("NullableGov",
    NullableGetValueOrDefaultSamples.NativeGov,
    NullableGetValueOrDefaultSamples.GuardedGov,
    new int?[] { null, 0, 42 });
foreach (var a in new int?[] { null, 0, 42 })
{
    CheckPair($"NullableGovDefault({GridLabel(a)})",
        NullableGetValueOrDefaultSamples.NativeGovDefault(a, 5),
        NullableGetValueOrDefaultSamples.GuardedGovDefault(a, 5));
}

var nativeProbeHasValue = new NullableDefaultProbe();
Check("NullableGovSideEffectHasValue",
    NullableGetValueOrDefaultSamples.NativeGovSideEffect(42, nativeProbeHasValue) == 42
    && nativeProbeHasValue.Evaluations == 1);
var nativeProbeNull = new NullableDefaultProbe();
Check("NullableGovSideEffectNull",
    NullableGetValueOrDefaultSamples.NativeGovSideEffect(null, nativeProbeNull) == 7
    && nativeProbeNull.Evaluations == 1);
var guardedProbeHasValue = new NullableDefaultProbe();
Check("NullableGuardedGovSideEffectHasValue",
    NullableGetValueOrDefaultSamples.GuardedGovSideEffect(42, guardedProbeHasValue) == 42
    && guardedProbeHasValue.Evaluations == 1);
var guardedProbeNull = new NullableDefaultProbe();
Check("NullableGuardedGovSideEffectNull",
    NullableGetValueOrDefaultSamples.GuardedGovSideEffect(null, guardedProbeNull) == 7
    && guardedProbeNull.Evaluations == 1);

// ── NL-7 unwrap: .Value and unwrap cast both throw on null ──
Check("NullableUnwrapCastValue",
    NullableConversionSamples.NativeUnwrapCast(42) == 42
    && NullableConversionSamples.GuardedUnwrapCast(42) == 42
    && NullableConversionSamples.NativeUnwrapMember(42) == 42
    && NullableConversionSamples.GuardedUnwrapMember(42) == 42);
CheckThrows<InvalidOperationException>("NullableUnwrapCastNull",
    () => NullableConversionSamples.NativeUnwrapCast(null));
CheckThrows<InvalidOperationException>("NullableUnwrapMemberNull",
    () => NullableConversionSamples.NativeUnwrapMember(null));
CheckThrows<InvalidOperationException>("NullableGuardedUnwrapCastNull",
    () => NullableConversionSamples.GuardedUnwrapCast(null));
CheckThrows<InvalidOperationException>("NullableGuardedUnwrapMemberNull",
    () => NullableConversionSamples.GuardedUnwrapMember(null));

// ── NL-8 lifted conversions (256 narrows to 0 unchecked) ──
CheckGrid1("NullableToLong",
    NullableConversionSamples.NativeToLong, NullableConversionSamples.GuardedToLong,
    new int?[] { null, 42, -1 });
CheckGrid1("NullableToByte",
    NullableConversionSamples.NativeToByte, NullableConversionSamples.GuardedToByte,
    new int?[] { null, 255, 256, 1 });
CheckPair("NullableNarrowWrap",
    NullableConversionSamples.NativeToByte(256),
    NullableConversionSamples.GuardedToByte(256));
CheckGrid1("NullableToInt",
    NullableConversionSamples.NativeToInt, NullableConversionSamples.GuardedToInt,
    new double?[] { null, 2.9, -3.7 });

// ── NL-9 nullable enum backstop bit operations and ~ ──
var enumValues = new NullableLiftColor?[]
{
    null, NullableLiftColor.Red, NullableLiftColor.Green, NullableLiftColor.Mask,
};
CheckGrid2("NullableEnumBitAnd",
    NullableEnumBackstopSamples.NativeBitAnd,
    NullableEnumBackstopSamples.GuardedBitAnd, enumValues);
CheckGrid2("NullableEnumBitOr",
    NullableEnumBackstopSamples.NativeBitOr,
    NullableEnumBackstopSamples.GuardedBitOr, enumValues);
CheckGrid1("NullableEnumBitNot",
    NullableEnumBackstopSamples.NativeBitNot,
    NullableEnumBackstopSamples.GuardedBitNot, enumValues);
CheckGrid1("NullableEnumToInt",
    NullableEnumBackstopSamples.NativeEnumToInt,
    NullableEnumBackstopSamples.GuardedEnumToInt, enumValues);

// ── NL-10/NL-11 lifted increment and compound ──
void CheckIncrementPair(string name, int? start)
{
    var np = NullableIncrementSamples.NativePrefix(start);
    var gp = NullableIncrementSamples.GuardedPrefix(start);
    CheckPair($"{name}.prefix", np, gp);

    var no = NullableIncrementSamples.NativePostfix(start);
    var go = NullableIncrementSamples.GuardedPostfix(start);
    CheckPair($"{name}.postfixOld", no.oldValue, go.oldValue);
    CheckPair($"{name}.postfixNew", no.newValue, go.newValue);

    var nd = NullableIncrementSamples.NativePostfixDecrement(start);
    var gd = NullableIncrementSamples.GuardedPostfixDecrement(start);
    CheckPair($"{name}.postfixDecOld", nd.oldValue, gd.oldValue);
    CheckPair($"{name}.postfixDecNew", nd.newValue, gd.newValue);
}

CheckIncrementPair("IncNull", null);
CheckIncrementPair("IncValue", 41);
CheckIncrementPair("IncBoundary", int.MaxValue);
var boundaryPostfix = NullableIncrementSamples.NativePostfix(int.MaxValue);
CheckPair("IncBoundaryPostfixOldWrap", boundaryPostfix.oldValue, (int?)int.MaxValue);
CheckPair("IncBoundaryPostfixNewWrap", boundaryPostfix.newValue, (int?)int.MinValue);
var boundaryGuarded = NullableIncrementSamples.GuardedPostfix(int.MaxValue);
CheckPair("IncBoundaryGuardedPostfixWrap", boundaryPostfix.newValue, boundaryGuarded.newValue);

foreach (var x in new int?[] { null, 1, 40 })
{
    foreach (var y in new int?[] { null, 2 })
    {
        CheckPair($"NullableCompound({GridLabel(x)},{GridLabel(y)})",
            NullableIncrementSamples.NativeCompound(x, y),
            NullableIncrementSamples.GuardedCompound(x, y));
    }

    CheckPair($"NullableCompoundOneSided({GridLabel(x)})",
        NullableIncrementSamples.NativeCompoundOneSided(x, 2),
        NullableIncrementSamples.GuardedCompoundOneSided(x, 2));
}

if (failures > 0)
{
    Console.Error.WriteLine($"{failures} numeric-boundary sample failure(s).");
    return 1;
}

Console.WriteLine("numeric-boundary sample OK");
return 0;

void CheckThrows<TException>(string name, Action action) where TException : Exception
{
    try
    {
        action();
        Console.Error.WriteLine($"FAIL: {name} (expected {typeof(TException).Name})");
        failures++;
    }
    catch (TException)
    {
        // Expected.
    }
}
