namespace CSharpToJava.ConversionTests;

/// <summary>
/// Tiered gate for slow tests that exercise full conversion pipelines or spawn
/// external processes. Default <c>dotnet test</c> run skips these tests; set
/// <c>CS2J_RUN_SLOW_E2E=1</c> to execute them.
/// </summary>
internal static class SlowE2EGate
{
    public const string EnvVar = "CS2J_RUN_SLOW_E2E";

    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } v
        && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    public static string SkipReason(double observedSeconds)
    {
        var suffix = observedSeconds > 0
            ? $" (observed serial duration {observedSeconds}s)"
            : string.Empty;
        return $"Slow E2E gate: set environment variable {EnvVar}=1 to run this test{suffix}.";
    }
}

/// <summary>
/// <see cref="FactAttribute"/> variant of <see cref="SlowE2EGate"/>: executed only
/// when <see cref="SlowE2EGate.Enabled"/>. <paramref name="observedSeconds"/> records
/// the last measured serial duration for capacity planning.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SlowE2EFactAttribute : FactAttribute
{
    public SlowE2EFactAttribute(double observedSeconds = 0)
    {
        if (!SlowE2EGate.Enabled)
        {
            Skip = SlowE2EGate.SkipReason(observedSeconds);
        }
    }
}

/// <summary>
/// <see cref="TheoryAttribute"/> variant of <see cref="SlowE2EGate"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SlowE2ETheoryAttribute : TheoryAttribute
{
    public SlowE2ETheoryAttribute(double observedSeconds = 0)
    {
        if (!SlowE2EGate.Enabled)
        {
            Skip = SlowE2EGate.SkipReason(observedSeconds);
        }
    }
}
