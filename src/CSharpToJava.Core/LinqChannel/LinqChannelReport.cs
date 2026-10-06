using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// Serializes the channel report written to the intermediate root as
/// <c>cs2j-linq-channel-report.json</c> (design §4.6/§5.2, schemaVersion 2).
/// The legacy E16 fields keep their --linq-report names; <c>routing</c> and
/// <c>preservedChains</c> are supersets (D11).
/// </summary>
public sealed class LinqChannelReport
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion => 2;

    [JsonPropertyName("desugaredQueryCount")]
    public int DesugaredQueryCount { get; init; }

    [JsonPropertyName("rewrittenChainCount")]
    public int RewrittenChainCount { get; init; }

    [JsonPropertyName("rewrittenMethodCount")]
    public int RewrittenMethodCount { get; init; }

    [JsonPropertyName("residualFindings")]
    public int ResidualFindings { get; init; }

    [JsonPropertyName("routing")]
    public RoutingSection Routing { get; init; } = new();

    [JsonPropertyName("skippedChains")]
    public IReadOnlyList<SkippedChainEntry> SkippedChains { get; init; } = Array.Empty<SkippedChainEntry>();

    [JsonPropertyName("preservedChains")]
    public IReadOnlyList<LinqPreservedChain> PreservedChains { get; init; } = Array.Empty<LinqPreservedChain>();

    [JsonPropertyName("uncoveredOperators")]
    public IReadOnlyList<UncoveredOperatorEntry> UncoveredOperators { get; init; } = Array.Empty<UncoveredOperatorEntry>();

    /// <summary>JSON shape of the §4.6 routing block.</summary>
    public sealed class RoutingSection
    {
        [JsonPropertyName("enumerableChainsExpanded")]
        public int EnumerableChainsExpanded { get; init; }

        [JsonPropertyName("queryableChainsPreserved")]
        public int QueryableChainsPreserved { get; init; }

        [JsonPropertyName("expressionArgumentSites")]
        public int ExpressionArgumentSites { get; init; }

        [JsonPropertyName("mixedChainsPreserved")]
        public int MixedChainsPreserved { get; init; }

        [JsonPropertyName("ambiguousChains")]
        public int AmbiguousChains { get; init; }

        [JsonPropertyName("providerCustomCalls")]
        public int ProviderCustomCalls { get; init; }

        [JsonPropertyName("expressionQuotedChainsPreserved")]
        public int ExpressionQuotedChainsPreserved { get; init; }

        [JsonPropertyName("expressionTreeUnprovableSites")]
        public int ExpressionTreeUnprovableSites { get; init; }

        [JsonPropertyName("groupByChainsPreserved")]
        public int GroupByChainsPreserved { get; init; }
    }

    public sealed record SkippedChainEntry(
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("method")] string? Method,
        [property: JsonPropertyName("message")] string? Message);

    public sealed record UncoveredOperatorEntry(
        [property: JsonPropertyName("operator")] string Operator,
        [property: JsonPropertyName("occurrences")] int Occurrences);

    public static LinqChannelReport From(LinqChannelStatistics statistics)
    {
        var rewrite = statistics.Rewrite;
        return new LinqChannelReport
        {
            DesugaredQueryCount = rewrite.DesugaredQueryCount,
            RewrittenChainCount = rewrite.RewrittenChainCount,
            RewrittenMethodCount = rewrite.RewrittenMethodCount,
            ResidualFindings = statistics.ResidualFindings,
            Routing = new RoutingSection
            {
                EnumerableChainsExpanded = statistics.EnumerableChainsExpanded,
                QueryableChainsPreserved = statistics.QueryableChainsPreserved,
                ExpressionArgumentSites = statistics.ExpressionArgumentSites,
                MixedChainsPreserved = statistics.MixedChainsPreserved,
                AmbiguousChains = statistics.AmbiguousChains,
                ProviderCustomCalls = statistics.ProviderCustomCalls,
                ExpressionQuotedChainsPreserved = statistics.ExpressionQuotedChainsPreserved,
                ExpressionTreeUnprovableSites = statistics.ExpressionTreeUnprovableSites,
                GroupByChainsPreserved = statistics.GroupByChainsPreserved,
            },
            SkippedChains = rewrite.SkippedChains
                .Select(s => new SkippedChainEntry(s.Reason.ToString(), s.LineNumber, s.MethodName, s.Message))
                .ToArray(),
            PreservedChains = statistics.PreservedChains.ToArray(),
            UncoveredOperators = rewrite.GetUncoveredOperatorsByFrequency()
                .Select(kv => new UncoveredOperatorEntry(kv.Key, kv.Value))
                .ToArray(),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Writes the report as UTF-8 (no BOM), overwriting any prior file.</summary>
    public void WriteToFile(string path)
    {
        File.WriteAllText(path, ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
