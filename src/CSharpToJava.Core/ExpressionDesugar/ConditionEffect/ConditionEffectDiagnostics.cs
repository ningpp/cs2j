using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.ConditionEffect;

/// <summary>
/// Diagnostic codes owned by the condition-effect rule. CS2J0590 was unused in
/// the source tree (C36 owns 0570–0589); the rule only reports conservative
/// skips, so every code is a Warning.
/// </summary>
internal static class ConditionEffectDiagnosticCodes
{
    /// <summary>Warning: a condition with side-effect syntax was preserved because its shape is out of this slice.</summary>
    public const string CS2J0590 = "CS2J0590";
}

/// <summary>Message templates for CS2J0590.</summary>
internal static class ConditionEffectDiagnostics
{
    /// <summary>
    /// {0} = the preserved condition text, {1} = why it was preserved
    /// (declaration/pattern variable, await, or an analysis failure).
    /// </summary>
    public const string SkippedMessage =
        "Condition '{0}' carries property/indexer side effects but was left " +
        "unchanged ({1}); the first slice only linearizes while/if conditions " +
        "without declaration expressions, pattern variables or await.";
}
