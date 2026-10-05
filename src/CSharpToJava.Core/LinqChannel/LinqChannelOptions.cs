namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// Options for the C26 linq-channel pass. Deliberately has NO Stream option
/// (D8: <c>PreferStreamApi</c> is a dead switch and the channel produces the
/// procedural envelope only).
/// </summary>
public sealed class LinqChannelOptions
{
    /// <summary>
    /// When true, routing ambiguities (LC0010) and residual violations (LC0040)
    /// are Errors instead of Warnings (Q5). Default false, matching every
    /// other preprocess pass.
    /// </summary>
    public bool Strict { get; init; }

    // M3 (design §4.6/§9): the route gate is permanently intercepting inside
    // the channel — the M1/M2 EnableQueryableGate observe-only off-switch was
    // removed. The three gates (Queryable preserve, Expression-quote preserve,
    // Ambiguous preserve) are unclosable before default-on. The embedded E16
    // construction still passes no gate at all (null gate = zero change).

    /// <summary>
    /// Optional explicit destination for a copy of the channel report. The
    /// report is always written to <c>cs2j-linq-channel-report.json</c> in the
    /// intermediate root regardless of this value (design §5.2).
    /// </summary>
    public string? ReportPath { get; init; }

    /// <summary>Injected by the adapter from the chain-wide verify switches.</summary>
    public bool VerifyBuild { get; init; } = true;

    /// <summary>Injected by the adapter from the chain-wide verify switches.</summary>
    public bool VerifyTests { get; init; } = true;
}
