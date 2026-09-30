namespace CSharpToJava.Core.IndexRange;

/// <summary>Options for the index-range (<c>^</c>/<c>..</c>) preprocessor.</summary>
public sealed class IndexRangeOptions
{
    /// <summary>
    /// When true (default), unresolvable sites and residual <c>^</c>/<c>..</c>
    /// nodes are reported as errors; when false, they degrade to warnings.
    /// </summary>
    public bool Strict { get; init; } = true;

    /// <summary>Default options instance.</summary>
    public static IndexRangeOptions Default { get; } = new();
}
