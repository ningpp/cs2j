namespace CSharpToJava.ProjectTests;

/// <summary>
/// Tiered gate for slow end-to-end tests (solution build -> Java conversion ->
/// javac -> JVM run). These tests take tens to hundreds of seconds each because
/// they spawn cold dotnet/javac/java processes.
///
/// Fast gate (default): <c>dotnet test</c> reports them as skipped, keeping the
/// PR feedback run at ~11 minutes.
/// Slow/nightly gate: set <c>CS2J_RUN_SLOW_E2E=1</c> to execute them
/// (<c>$env:CS2J_RUN_SLOW_E2E=1; dotnet test --filter ...</c>).
///
/// The rule logic each E2E exercises stays covered by fast isolated unit tests;
/// this gate only shelves the full-process chain.
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
        return $"Slow E2E gate: set environment variable {EnvVar}=1 to run the full " +
               $"convert/build/javac/run chain{suffix}.";
    }
}

/// <summary>
/// <see cref="FactAttribute"/> variant of <see cref="SlowE2EGate"/>: executed only
/// on the slow/nightly gate. <paramref name="observedSeconds"/> records the last
/// measured serial duration for capacity planning.
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
/// <see cref="TheoryAttribute"/> variant of <see cref="SlowE2EGate"/>: executed
/// only on the slow/nightly gate.
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
