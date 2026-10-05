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

    /// <summary>
    /// Whether the observe-only route gate is attached to <c>LinqRewriter</c>.
    /// M1: the gate only records routing decisions/counters and never changes
    /// rewrite output; it becomes a hard gate in M3, where the off-switch is
    /// removed (design §4.6).
    /// </summary>
    public bool EnableQueryableGate { get; init; } = true;

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
