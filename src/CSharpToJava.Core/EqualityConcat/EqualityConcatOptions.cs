namespace CSharpToJava.Core.EqualityConcat;

/// <summary>
/// Options for the C47 equality/concat call-site preprocessor.
/// </summary>
public sealed class EqualityConcatOptions
{
    /// <summary>
    /// When true (default), residual sites CS2J0474/CS2J0475 are reported as
    /// errors; when false they degrade to warnings. CS2J0476 stays a warning.
    /// </summary>
    public bool Strict { get; init; } = true;

    /// <summary>
    /// Expression-tree sites are always skipped (design §2 decision 14);
    /// the switch is retained only for diagnostic classification.
    /// </summary>
    public bool SkipExpressionTrees { get; init; } = true;

    /// <summary>
    /// When true (default), delegate <c>==</c>/<c>!=</c> sites are rewritten
    /// to <c>Cs2jPrelude.DelegateOps.Equals</c>; when false such sites degrade
    /// to CS2J0473 warnings (controlled degradation until the Java half J4
    /// lands), never to a bare identity comparison.
    /// </summary>
    public bool EnableDelegateRule { get; init; } = true;

    /// <summary>Default options instance.</summary>
    public static EqualityConcatOptions Default { get; } = new();
}
