namespace SampleNumericBoundaryLib.Nullable;

/// <summary>
/// C22 M2 NL-4 oracle fixture: lifted equality split into the three operand
/// shapes — raw <c>x == null</c> (renders as the HasValue path), one-sided
/// <c>x == v</c>, and the both-nullable nine-cell grid whose special case is
/// <c>null == null → true</c> (unlike the relational <c>null &gt;= null</c>).
/// </summary>
public static class NullableEqualitySamples
{
    // ── both-nullable grid (double null compares equal) ──

    public static bool NativeEqual(int? a, int? b) => a == b;

    public static bool GuardedEqual(int? a, int? b) =>
        (!a.HasValue && !b.HasValue)
        || (a.HasValue && b.HasValue && a.Value == b.Value);

    public static bool NativeNotEqual(int? a, int? b) => a != b;

    public static bool GuardedNotEqual(int? a, int? b) =>
        (a.HasValue ^ b.HasValue)
        || (a.HasValue && b.HasValue && a.Value != b.Value);

    // ── one-sided: exactly one nullable operand ──

    public static bool NativeEqualOneSided(int? a, int b) => a == b;

    public static bool GuardedEqualOneSided(int? a, int b) =>
        a.HasValue && a.Value == b;

    public static bool NativeNotEqualOneSided(int? a, int b) => a != b;

    public static bool GuardedNotEqualOneSided(int? a, int b) =>
        !a.HasValue || a.Value != b;

    // bool? against a bool literal is an equality site too.
    public static bool NativeBoolEqualTrue(bool? a) => a == true;

    public static bool GuardedBoolEqualTrue(bool? a) =>
        a.HasValue && a.Value;

    // ── raw null-literal equality: never a lifted binding ──

    public static bool NativeIsNull(int? a) => a == null;

    public static bool GuardedIsNull(int? a) => !a.HasValue;

    public static bool NativeIsNotNull(int? a) => a != null;

    public static bool GuardedIsNotNull(int? a) => a.HasValue;
}
