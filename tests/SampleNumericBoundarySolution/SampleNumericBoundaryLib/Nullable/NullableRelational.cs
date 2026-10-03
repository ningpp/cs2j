namespace SampleNumericBoundaryLib.Nullable;

/// <summary>
/// C22 M2 NL-3 oracle fixture: lifted relational operators on
/// <c>int?</c> return a plain (non-nullable) <c>bool</c> — any null operand
/// makes the relation false, including the counter-intuitive
/// <c>null &gt;= null == false</c>. Each <c>Native*</c> member pairs with a
/// hand-written <c>Guarded*</c> HasValue shape.
/// </summary>
public static class NullableRelationalSamples
{
    public static bool NativeGreaterThanOrEqual(int? a, int? b) => a >= b;

    public static bool GuardedGreaterThanOrEqual(int? a, int? b) =>
        a.HasValue && b.HasValue && a.Value >= b.Value;

    public static bool NativeGreaterThan(int? a, int? b) => a > b;

    public static bool GuardedGreaterThan(int? a, int? b) =>
        a.HasValue && b.HasValue && a.Value > b.Value;

    public static bool NativeLessThanOrEqual(int? a, int? b) => a <= b;

    public static bool GuardedLessThanOrEqual(int? a, int? b) =>
        a.HasValue && b.HasValue && a.Value <= b.Value;

    public static bool NativeLessThan(int? a, int? b) => a < b;

    public static bool GuardedLessThan(int? a, int? b) =>
        a.HasValue && b.HasValue && a.Value < b.Value;

    // One-sided forms (non-nullable right operand): only the left side carries
    // a HasValue guard.
    public static bool NativeGreaterThanOrEqualOneSided(int? a, int b) => a >= b;

    public static bool GuardedGreaterThanOrEqualOneSided(int? a, int b) =>
        a.HasValue && a.Value >= b;

    public static bool NativeLessThanOneSided(int? a, int b) => a < b;

    public static bool GuardedLessThanOneSided(int? a, int b) =>
        a.HasValue && a.Value < b;
}
