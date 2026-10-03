namespace SampleNumericBoundaryLib.Nullable;

/// <summary>
/// C22 M2 NL-7/NL-8 oracle fixture: <c>T? → T</c> unwrap conversions render
/// as <c>.Value</c> (null throws <c>InvalidOperationException</c>, whether the
/// source shape is an unwrap cast or a direct <c>.Value</c> access), and
/// <c>S? → T?</c> lifted conversions become HasValue/Value guards. The
/// library is unchecked, so narrowing cells wrap (<c>(byte?)256 → 0</c>).
/// </summary>
public static class NullableConversionSamples
{
    // ── NL-7 unwrap: both source shapes throw on null, identical when set ──

    public static int NativeUnwrapCast(int? a) => (int)a;

    public static int GuardedUnwrapCast(int? a) => a.Value;

    public static int NativeUnwrapMember(int? a) => a.Value;

    public static int GuardedUnwrapMember(int? a) => a.Value;

    // ── NL-8 lifted widening conversion ──

    public static long? NativeToLong(int? a) => (long?)a;

    public static long? GuardedToLong(int? a) =>
        a.HasValue ? (long?)(long)a.Value : (long?)null;

    // ── NL-8 lifted narrowing conversion (unchecked wrap: 256 → (byte)0) ──

    public static byte? NativeToByte(int? a) => (byte?)a;

    public static byte? GuardedToByte(int? a) =>
        a.HasValue ? (byte?)(byte)a.Value : (byte?)null;

    // ── NL-8 lifted unwrap-and-convert (double? → int?) ──

    public static int? NativeToInt(double? a) => (int?)a;

    public static int? GuardedToInt(double? a) =>
        a.HasValue ? (int?)(int)a.Value : (int?)null;
}
