namespace CSharpToJava.Core.LinqRewrite;

/// <summary>
/// Stable classification for why a LINQ chain was skipped during rewriting.
/// </summary>
public enum LinqSkipReason
{
    /// <summary>Chain contains a method not yet implemented in rules.</summary>
    UnsupportedMethodChain,

    /// <summary>An implemented operator was called with an unsupported overload.</summary>
    UnsupportedOverload,

    /// <summary>Anonymous types cannot be expressed without record support.</summary>
    AnonymousTypeRequiresRecords,

    /// <summary>The semantic model was unavailable for the file.</summary>
    SemanticModelUnavailable,

    /// <summary>Rule expansion threw an exception at runtime.</summary>
    RuleExpansionFailed,

    /// <summary>The chain has no lambda and no recognized non-lambda intermediate/terminal.</summary>
    NoLambdaOrRecognizedOperator,

    /// <summary>A single root method that requires yield-return was the only step.</summary>
    SingleRootMethodRequiresYield,

    /// <summary>Return type could not be resolved from the semantic model.</summary>
    ReturnTypeUnresolved,

    /// <summary>Non-lambda argument passed to a method that only accepts lambdas.</summary>
    NonLambdaArgument,
}

/// <summary>
/// Structured information about a skipped LINQ chain.
/// </summary>
public sealed record LinqSkipInfo(
    LinqSkipReason Reason,
    int LineNumber,
    string? MethodName,
    string Message);

/// <summary>
/// Records a LINQ operator that was encountered during rewriting.
/// </summary>
public sealed record LinqOperatorOccurrence(
    string MethodFullName,
    int LineNumber,
    bool WasRewritten);

/// <summary>
/// Aggregated LINQ rewrite statistics for a single file or an entire project.
/// </summary>
public sealed class LinqRewriteStatistics
{
    /// <summary>Number of query expressions desugared (from…select → method chain).</summary>
    public int DesugaredQueryCount { get; set; }

    /// <summary>Number of method chains successfully rewritten to procedural code.</summary>
    public int RewrittenChainCount { get; set; }

    /// <summary>Number of methods that contained at least one rewritten chain.</summary>
    public int RewrittenMethodCount { get; set; }

    /// <summary>Structured list of skipped chains with classification.</summary>
    public List<LinqSkipInfo> SkippedChains { get; } = [];

    /// <summary>All LINQ operators encountered during rewriting, with rewrite outcome.</summary>
    public List<LinqOperatorOccurrence> EncounteredOperators { get; } = [];

    /// <summary>
    /// Returns the uncovered operators sorted descending by occurrence count.
    /// An uncovered operator is one that was encountered but never successfully rewritten.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, int>> GetUncoveredOperatorsByFrequency()
    {
        // Operators that were rewritten at least once
        var rewrittenOps = new HashSet<string>(
            EncounteredOperators.Where(o => o.WasRewritten).Select(o => o.MethodFullName));

        // Count occurrences of operators never rewritten
        return EncounteredOperators
            .Where(o => !o.WasRewritten && !rewrittenOps.Contains(o.MethodFullName))
            .GroupBy(o => o.MethodFullName)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Merges another statistics instance into this one (for project-level aggregation).
    /// </summary>
    public void MergeFrom(LinqRewriteStatistics other)
    {
        DesugaredQueryCount += other.DesugaredQueryCount;
        RewrittenChainCount += other.RewrittenChainCount;
        RewrittenMethodCount += other.RewrittenMethodCount;
        SkippedChains.AddRange(other.SkippedChains);
        EncounteredOperators.AddRange(other.EncounteredOperators);
    }
}
