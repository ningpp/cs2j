namespace SampleNumericBoundaryLib.Nullable;

/// <summary>
/// C22 M2 NL-1/NL-2 oracle fixture (lifted arithmetic and unary operators).
/// Every <c>Native*</c> member is the C# compiler's own lifted
/// <c>Nullable&lt;T&gt;</c> semantics; the paired <c>Guarded*</c> member is
/// the hand-written fixed sample of the explicitized HasValue/Value shape.
/// The library compiles in the default unchecked context, so boundary cells
/// wrap in both versions (int.MaxValue + int.MaxValue == -2).
/// </summary>
public static class NullableArithmeticSamples
{
    // ── NL-1 lifted binary arithmetic ──

    public static int? NativeAdd(int? a, int? b) => a + b;

    public static int? GuardedAdd(int? a, int? b) =>
        a.HasValue && b.HasValue ? (int?)(a.Value + b.Value) : (int?)null;

    public static int? NativeAddOneSided(int? a, int b) => a + b;

    public static int? GuardedAddOneSided(int? a, int b) =>
        a.HasValue ? (int?)(a.Value + b) : (int?)null;

    public static long? NativeSubtract(long? a, long? b) => a - b;

    public static long? GuardedSubtract(long? a, long? b) =>
        a.HasValue && b.HasValue ? (long?)(a.Value - b.Value) : (long?)null;

    public static double? NativeMultiply(double? a, double b) => a * b;

    public static double? GuardedMultiply(double? a, double b) =>
        a.HasValue ? (double?)(a.Value * b) : (double?)null;

    // ── NL-2 lifted unary operators ──

    public static int? NativeNegate(int? a) => -a;

    public static int? GuardedNegate(int? a) =>
        a.HasValue ? (int?)(-a.Value) : (int?)null;

    public static long? NativeUnaryPlus(long? a) => +a;

    public static long? GuardedUnaryPlus(long? a) =>
        a.HasValue ? (long?)(+a.Value) : (long?)null;
}
