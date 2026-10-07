namespace SampleNumericBoundaryLib.Nullable;

/// <summary>
/// C22 M2 NL-9 fixture enum used by the nullable lifted bit-operator samples.
/// </summary>
public enum NullableLiftColor
{
    Red = 0,
    Green = 1,
    Blue = 2,
    Mask = 3,
}

/// <summary>
/// C22 M2 NL-9 oracle fixture: lifted bit operations and bitwise unary
/// <c>~</c> on nullable enums use the integer backstop core
/// <c>(E)(int)…</c>; <c>E? → int?</c> is the lifted conversion companion.
/// Null operands propagate exactly like the numeric lifted operators.
/// </summary>
public static class NullableEnumBackstopSamples
{
    public static NullableLiftColor? NativeBitAnd(
        NullableLiftColor? a, NullableLiftColor? b) => a & b;

    public static NullableLiftColor? GuardedBitAnd(
        NullableLiftColor? a, NullableLiftColor? b) =>
        a.HasValue && b.HasValue
            ? (NullableLiftColor?)(NullableLiftColor)((int)a.Value & (int)b.Value)
            : (NullableLiftColor?)null;

    public static NullableLiftColor? NativeBitOr(
        NullableLiftColor? a, NullableLiftColor? b) => a | b;

    public static NullableLiftColor? GuardedBitOr(
        NullableLiftColor? a, NullableLiftColor? b) =>
        a.HasValue && b.HasValue
            ? (NullableLiftColor?)(NullableLiftColor)((int)a.Value | (int)b.Value)
            : (NullableLiftColor?)null;

    // NB: the raw ~ result (-4 for Mask) has no named member; Java enums cannot
    // carry out-of-range constants, so both sides mask back into the declared
    // domain — the native/guarded equivalence (lifted vs HasValue-guarded) is
    // what this backstop pair pins.
    public static NullableLiftColor? NativeBitNot(NullableLiftColor? a) =>
        a.HasValue ? (NullableLiftColor?)(NullableLiftColor)(~((int)a.Value) & 3) : null;

    public static NullableLiftColor? GuardedBitNot(NullableLiftColor? a) =>
        a.HasValue
            ? (NullableLiftColor?)(NullableLiftColor)(~((int)a.Value) & 3)
            : (NullableLiftColor?)null;

    public static int? NativeEnumToInt(NullableLiftColor? a) => (int?)a;

    public static int? GuardedEnumToInt(NullableLiftColor? a) =>
        a.HasValue ? (int?)(int)a.Value : (int?)null;
}
