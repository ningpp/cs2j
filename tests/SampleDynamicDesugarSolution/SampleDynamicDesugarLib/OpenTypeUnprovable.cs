namespace SampleDynamicDesugarLib;

/// <summary>Open (non-sealed) receiver type used by the DS010 unprovable-local case.</summary>
public abstract class OpenWidget
{
    public abstract string Kind { get; }

    public string Describe() => Kind + "-widget";
}

public sealed class RoundWidget : OpenWidget
{
    public override string Kind => "round";
}

public static class OpenTypeUnprovable
{
    public static OpenWidget Create() => new RoundWidget();

    /// <summary>
    /// The local's single assignment has the static type <see cref="OpenWidget"/>
    /// (an open, non-sealed class): the P1 prover rejects it with
    /// <c>SealedNotSatisfied</c> (DS010), and the late-bound call under it is a
    /// registered residual (DS013). Both sites are byte-retained and listed in
    /// Cs2jDynamicAudit.json; runtime dispatch still returns "round-widget".
    /// </summary>
    public static string DescribeWidget()
    {
        dynamic widget = Create();
        return widget.Describe();
    }
}
