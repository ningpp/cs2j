namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// Per-category gray-release switches for the C24 initializer rule
/// (design §4 InitializerOptions). M0 analysis ignores the scope filter.
/// </summary>
public enum InitializerScope
{
    All,
    Object,
    Collection,
    TargetTypedNew,
}

/// <summary>
/// Policy for sites the rule cannot desugar equivalently (design §6
/// Error-class codes). <see cref="Error"/> fails the file atomically under
/// strict options; <see cref="Warning"/> preserves the node.
/// </summary>
public enum InitializerUnsupportedPolicy
{
    /// <summary>Error-class codes fail the file (atomically unchanged) under strict.</summary>
    Error,

    /// <summary>Downgrade Error-class codes to Warnings and preserve the node.</summary>
    Warning,
}

/// <summary>
/// Options for the C24 object/collection-initializer rule (design §4).
/// </summary>
public sealed class InitializerOptions
{
    /// <summary>Error-class codes fail the file (atomically unchanged); false degrades them to warnings.</summary>
    public bool Strict { get; init; } = true;

    /// <summary>Category gray-release scope (M1+; M0 analysis ignores the scope filter).</summary>
    public InitializerScope Scope { get; init; } = InitializerScope.All;

    /// <summary>
    /// Expression-tree lambda policy. False (default) skips initializer sites
    /// inside <c>Expression&lt;T&gt;</c> lambdas (CS2J0542), matching the
    /// family-wide <c>IsInExpressionTree</c> criterion.
    /// </summary>
    public bool RewriteInExpressionTrees { get; init; }

    /// <summary>Unsupported-site policy; defaults to Error (strict).</summary>
    public InitializerUnsupportedPolicy Unsupported { get; init; } =
        InitializerUnsupportedPolicy.Error;

    /// <summary>
    /// Chain-composition flag: true when the member-desugar pass (C11/C27)
    /// runs later in the same preprocess chain. It widens init-only
    /// accessors to ordinary set methods and lifts object initializers to
    /// setter calls (PD-8), so an init-only assignment left in place by
    /// this rule (CS2J0531) is reported as a Warning/preserved site rather
    /// than a strict Error. Other non-writable forms (no setter, static,
    /// readonly field) stay Errors — member-desugar cannot rescue those.
    /// Default false preserves the standalone strict behavior.
    /// </summary>
    public bool MemberDesugarChainAvailable { get; init; }

    /// <summary>Default options instance.</summary>
    public static InitializerOptions Default { get; } = new();
}