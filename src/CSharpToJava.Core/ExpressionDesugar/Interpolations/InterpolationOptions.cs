namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// Policy for interpolation sites bound to a custom
/// <c>[InterpolatedStringHandler]</c> type (hard gate G0, design §2.1/§4.3,
/// open question Q1: Error/Keep only — no emulation whitelist).
/// </summary>
public enum HandlerUnsupportedPolicy
{
    /// <summary>CS2J0510 Error under strict options; the file is atomically unchanged.</summary>
    Error,

    /// <summary>CS2J0510 Warning and the node is explicitly preserved (IE-KEEP).</summary>
    Keep,
}

/// <summary>
/// Options for the C23 interpolation-expansion rule (design §4.2). The
/// expression-desugar pass itself is already default-enabled (it carries the
/// C36 rule); C23 is therefore an explicit rule-level opt-in
/// (<see cref="Enabled"/> defaults to <c>false</c>) so turning the family on
/// never silently changes interpolation behavior before the C23 rollout.
/// </summary>
public sealed class InterpolationOptions
{
    /// <summary>
    /// Rule-level gray-release switch. When false the rule never enters the
    /// active set (its dirty gate reports clean even for files containing
    /// interpolations), preserving byte-identical pre-C23 behavior.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Error-class codes fail the file (atomically unchanged); false degrades them to warnings.</summary>
    public bool Strict { get; init; } = true;

    /// <summary>Policy for custom interpolated-string-handler bindings (Q1: Error/Keep only).</summary>
    public HandlerUnsupportedPolicy HandlerPolicy { get; init; } = HandlerUnsupportedPolicy.Error;

    /// <summary>
    /// Keep <c>System.FormattableString</c>/<c>IFormattableString</c> bound
    /// sites verbatim (CS2J0511). The user explicitly chose the runtime
    /// shape; expanding it would destroy the composite/argument array.
    /// </summary>
    public bool KeepFormattableString { get; init; } = true;

    /// <summary>
    /// Rewrite interpolations inside <c>Expression&lt;T&gt;</c> lambdas.
    /// Default false: such sites are preserved with CS2J0515 (Q4 defers
    /// compiler-canonical lowering to a later milestone).
    /// </summary>
    public bool RewriteInExpressionTrees { get; init; } = false;

    /// <summary>Fold fully constant interpolations to a bare string literal (IE-FOLD).</summary>
    public bool FoldConstants { get; init; } = true;

    /// <summary>Default options instance.</summary>
    public static InterpolationOptions Default { get; } = new();
}
