namespace SampleNumericBoundaryLib.Nullable;

/// <summary>
/// C22 M2 NL-6 oracle fixture: the GOV accessor (zero- and one-argument
/// form). Note the BCL method is a real instance method,
/// so a non-constant default argument is evaluated eagerly even when the
/// receiver <c>HasValue</c>; the explicitized shape hoists it into a
/// single-evaluation <c>d</c> temporary to preserve that exact behavior.
/// </summary>
public static class NullableGetValueOrDefaultSamples
{
    public static int NativeGov(int? a) => a.GetValueOrDefault();

    public static int GuardedGov(int? a) =>
        a.HasValue ? a.Value : default(int);

    public static int NativeGovDefault(int? a, int d) => a.GetValueOrDefault(d);

    public static int GuardedGovDefault(int? a, int d) =>
        a.HasValue ? a.Value : d;

    public static int NativeGovSideEffect(int? a, NullableDefaultProbe probe)
    {
        return a.GetValueOrDefault(probe.Next());
    }

    public static int GuardedGovSideEffect(int? a, NullableDefaultProbe probe)
    {
        // Hoisted d-role temp: evaluated once, eagerly, exactly like the
        // real call's argument passing.
        var d = probe.Next();
        return a.HasValue ? a.Value : d;
    }
}

/// <summary>Counts evaluations of the GOV default argument (single-evaluation probe).</summary>
public sealed class NullableDefaultProbe
{
    public int Evaluations { get; private set; }

    public int Next()
    {
        Evaluations++;
        return 7;
    }
}
