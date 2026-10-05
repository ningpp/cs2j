using CSharpToJava.Core.LinqRewrite;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// One preserved LINQ chain entry for the JSON report (design §4.6).
/// </summary>
public sealed record LinqPreservedChain(
    string File,
    int Line,
    string Route,
    string? Host,
    IReadOnlyList<string> Operators);

/// <summary>
/// Aggregated channel statistics for one file/project/run. Wraps the existing
/// <see cref="LinqRewriteStatistics"/> (so --linq-report consumers keep their
/// fields) and adds the v2 routing counters and preserved-chain inventory
/// (D11, schemaVersion 2).
/// </summary>
public sealed class LinqChannelStatistics
{
    /// <summary>Embedded E16-compatible counters (desugared/rewritten/skipped/uncovered).</summary>
    public LinqRewriteStatistics Rewrite { get; } = new();

    /// <summary>Chains routed Expandable (all nodes Enumerable/NonLinq).</summary>
    public int EnumerableChainsExpanded { get; set; }

    /// <summary>Chains whose LINQ nodes all bind to System.Linq.Queryable.</summary>
    public int QueryableChainsPreserved { get; set; }

    /// <summary>Argument sites whose converted type is Expression&lt;T&gt; (D2).</summary>
    public int ExpressionArgumentSites { get; set; }

    /// <summary>Chains crossing Enumerable and Queryable hosts, preserved wholesale (D6).</summary>
    public int MixedChainsPreserved { get; set; }

    /// <summary>Chains containing an Ambiguous node, conservatively preserved (LC0010).</summary>
    public int AmbiguousChains { get; set; }

    /// <summary>
    /// Member-access calls with a LINQ-lookalike operator name bound to a
    /// non-BCL host (e.g. a custom <c>Where</c> extension); counted/reported,
    /// never touched (D3②).
    /// </summary>
    public int ProviderCustomCalls { get; set; }

    /// <summary>
    /// M3: otherwise-expandable chains lexically inside an
    /// <c>Expression&lt;&gt;</c>-quoted lambda; every node is frozen (design
    /// §4.4 third route; fixes the M2 leftover where a quoted inner
    /// Enumerable chain was proceduralized).
    /// </summary>
    public int ExpressionQuotedChainsPreserved { get; set; }

    /// <summary>
    /// M3 LC0012: invocation sites inside quoted expression trees whose
    /// MethodInfo identity the channel cannot prove (calls bound outside the
    /// BCL <c>System.*</c> surface, or unbound calls); preserved and reported.
    /// </summary>
    public int ExpressionTreeUnprovableSites { get; set; }

    /// <summary>Inventory of every non-expandable chain (Queryable/Mixed/Ambiguous).</summary>
    public List<LinqPreservedChain> PreservedChains { get; } = new();

    /// <summary>Merges another instance into this one (cross-file/cross-project aggregation).</summary>
    public void MergeFrom(LinqChannelStatistics other)
    {
        Rewrite.MergeFrom(other.Rewrite);
        EnumerableChainsExpanded += other.EnumerableChainsExpanded;
        QueryableChainsPreserved += other.QueryableChainsPreserved;
        ExpressionArgumentSites += other.ExpressionArgumentSites;
        MixedChainsPreserved += other.MixedChainsPreserved;
        AmbiguousChains += other.AmbiguousChains;
        ProviderCustomCalls += other.ProviderCustomCalls;
        ExpressionQuotedChainsPreserved += other.ExpressionQuotedChainsPreserved;
        ExpressionTreeUnprovableSites += other.ExpressionTreeUnprovableSites;
        PreservedChains.AddRange(other.PreservedChains);
    }
}
