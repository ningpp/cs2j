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
    /// Switches the route gate mode at <c>LinqRewriter</c>'s two entry points
    /// (design §4.3/§4.6).
    /// <list type="bullet">
    /// <item><b>true (default, M2 intercept)</b>: Queryable/ExpressionArgument/
    /// Mixed/Ambiguous chains early-exit through <c>ShouldPreserve</c> and are
    /// kept verbatim (Ambiguous additionally reports LC0010); Enumerable/NonLinq
    /// chains expand exactly as the embedded E16 rewriter.</item>
    /// <item><b>false (observe-only, M1 shape)</b>: the gate only records
    /// routing decisions/counters and never changes a rewrite decision; output
    /// is byte-identical to an ungated E16 run.</item>
    /// </list>
    /// M3 forces this to true and removes the off-switch (the three gates
    /// become unclosable before default-on, design §4.6/§9).
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
