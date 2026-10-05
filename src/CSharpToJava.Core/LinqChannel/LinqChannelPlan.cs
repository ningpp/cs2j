using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// Per-file route plan: every member-access LINQ chain in the (post-desugar)
/// tree with its node-level routing decisions (design §4.1/§4.5 step 4). The
/// plan is shared by the observe-only gate seam, the summary/report counters
/// and (M3) the residual scan.
/// </summary>
public sealed class LinqChannelPlan
{
    private LinqChannelPlan(string? filePath, IReadOnlyList<ChainEntry> chains)
    {
        FilePath = filePath;
        Chains = chains;
    }

    /// <summary>Project-relative path when known (reports/diagnostics); null for in-memory runs.</summary>
    public string? FilePath { get; }

    /// <summary>All LINQ chains discovered in the file (outermost node first).</summary>
    public IReadOnlyList<ChainEntry> Chains { get; }

    /// <summary>True when the file contains at least one LINQ chain (R9 fast-skip signal).</summary>
    public bool HasLinqChains => Chains.Count > 0;

    /// <summary>One discovered chain with its syntax nodes and routing decision.</summary>
    public sealed record ChainEntry(
        IReadOnlyList<InvocationExpressionSyntax> Nodes,
        LinqChainDecision Decision);

    /// <summary>
    /// Builds the plan from a semantic model over a post-desugar syntax root.
    /// Chain discovery is purely syntactic; routing is purely semantic.
    /// </summary>
    public static LinqChannelPlan Build(SemanticModel semanticModel, SyntaxNode root, string? filePath)
    {
        var classifier = new LinqRouteClassifier(semanticModel);
        var entries = new List<ChainEntry>();
        foreach (var chain in LinqRouteClassifier.FindChains(root))
        {
            var decision = classifier.ClassifyChain(chain);
            entries.Add(new ChainEntry(chain, decision));
        }

        return new LinqChannelPlan(filePath, entries);
    }
}
