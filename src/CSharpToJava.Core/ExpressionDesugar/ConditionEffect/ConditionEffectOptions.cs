namespace CSharpToJava.Core.ExpressionDesugar.ConditionEffect;

/// <summary>
/// Options for the J-04 / G-36 / JE-04 condition-effect rule. The subsection
/// is nullable on <see cref="ExpressionDesugarOptions"/>; absent = the rule
/// stays inert (same rollout gate as C24/C35). <see cref="Enabled"/> defaults
/// to true so the CLI's default construction activates the rule.
/// </summary>
public sealed class ConditionEffectOptions
{
    /// <summary>Rule-level switch; false keeps the rule inert without removing the subsection.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Default options instance.</summary>
    public static ConditionEffectOptions Default { get; } = new();
}
