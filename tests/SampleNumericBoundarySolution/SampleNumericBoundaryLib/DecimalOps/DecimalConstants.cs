namespace SampleNumericBoundaryLib.DecimalOps;

/// <summary>
/// C34 DC-12 fixture: const-decimal declarators whose initializer contains a
/// rewritten decimal operator (or that reference such a constant) are
/// downgraded to static readonly so the initializer can run at type-init time.
/// Pure literal constants stay const.
/// </summary>
public static class DecimalConstants
{
    public const decimal Pure = 1.5m;

    public const decimal Factor = 2m;

    public const decimal Seeded = Factor * 3m;

    public const decimal CascadedFromSeeded = Seeded;

    public const decimal CascadedFromFactor = Factor + 1m;

    public static decimal LocalMix()
    {
        const decimal localPure = 3.25m;
        const decimal localSeeded = Seeded + 1m;
        const decimal keep = 9m, downgraded = localPure * 2m;
        return localPure + localSeeded + keep + downgraded;
    }
}
