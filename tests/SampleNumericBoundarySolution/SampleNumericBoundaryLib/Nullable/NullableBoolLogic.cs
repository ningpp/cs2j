namespace SampleNumericBoundaryLib.Nullable;

/// <summary>
/// C22 M2 NL-5 oracle fixture: lifted three-valued logic on <c>bool?</c>.
/// <c>&amp;</c> has false-absorption (<c>false &amp; null → false</c>),
/// <c>|</c> has true-absorption (<c>true | null → true</c>), <c>^</c> simply
/// propagates null and lifted <c>!</c> flips only when a value exists.
/// </summary>
public static class NullableBoolLogicSamples
{
    public static bool? NativeAnd(bool? a, bool? b) => a & b;

    public static bool? GuardedAnd(bool? a, bool? b)
    {
        // false absorbs even a null operand; otherwise null propagates.
        if ((a.HasValue && !a.Value) || (b.HasValue && !b.Value))
        {
            return false;
        }

        return a.HasValue && b.HasValue ? true : (bool?)null;
    }

    public static bool? NativeOr(bool? a, bool? b) => a | b;

    public static bool? GuardedOr(bool? a, bool? b)
    {
        // true absorbs even a null operand; otherwise null propagates.
        if ((a.HasValue && a.Value) || (b.HasValue && b.Value))
        {
            return true;
        }

        return a.HasValue && b.HasValue ? false : (bool?)null;
    }

    public static bool? NativeXor(bool? a, bool? b) => a ^ b;

    public static bool? GuardedXor(bool? a, bool? b) =>
        a.HasValue && b.HasValue ? (bool?)(a.Value ^ b.Value) : (bool?)null;

    public static bool? NativeNot(bool? a) => !a;

    public static bool? GuardedNot(bool? a) =>
        a.HasValue ? (bool?)(!a.Value) : (bool?)null;
}
