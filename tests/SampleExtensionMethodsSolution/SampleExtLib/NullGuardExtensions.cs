namespace SampleExtLib.NullGuards;

/// <summary>
/// C19 M2: nullable receivers. <see cref="IsNull"/> never dereferences the
/// receiver, so a null argument must not throw after explicitness;
/// <see cref="LengthOrNull"/> exercises the null-conditional lift shape.
/// </summary>
public static class NullGuardExtensions
{
    public static bool IsNull(this string? value) => value is null;

    public static int? LengthOrNull(this string? value) => value?.Length;
}
