namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// Options for the C35 null-conditional read-side rule (design §4/§5).
/// The subsection is nullable on <see cref="ExpressionDesugarOptions"/>;
/// absent = the rule stays inert (dirty trigger syntax but zero rewrite),
/// same rollout-gate pattern as C24 Initializers.
/// </summary>
public sealed class NullConditionalOptions
{
    /// <summary>Error-class codes fail the file atomically (unchanged bytes); false degrades them to warnings.</summary>
    public bool Strict { get; init; } = true;

    /// <summary>
    /// Expression-tree lambda policy. False (default) skips <c>?.</c>/<c>?[]</c>
    /// sites inside <c>Expression&lt;T&gt;</c> lambdas (CS2J0565), matching the
    /// family-wide <c>IsInExpressionTree</c> criterion (baseline illegal anyway).
    /// </summary>
    public bool RewriteInExpressionTrees { get; init; }

    /// <summary>Default options instance.</summary>
    public static NullConditionalOptions Default { get; } = new();
}
