using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using CSharpToJava.Core.ExpressionDesugar.Interpolations;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Fixed in-family rule order (design §3.2):
/// <list type="number">
/// <item>Interpolations (C23) — landed; opt-in via
/// <see cref="Interpolations.InterpolationOptions.Enabled"/> (the family
/// itself is default-on for C36, so C23 stays inert until enabled);</item>
/// <item>Initializers (C24) — not yet merged, append position reserved;</item>
/// <item>NullConditional (C35) — not yet merged, append position reserved;</item>
/// <item>CollectionExpr (C36) — registered as the M0 anchor.</item>
/// </list>
/// Rules run once per file in this order; append-only, never reorder.
/// </summary>
internal static class ExpressionDesugarRuleSet
{
    public static IReadOnlyList<IExpressionDesugarRule> Rules { get; } = new IExpressionDesugarRule[]
    {
        // C23 — IE-FOLD + IE-CONCAT + handler binding gate G0. Append first.
        new InterpolationRule(),
        // new InitializersRule(),     // C24 — append second
        // new NullConditionalRule(),  // C35 — append third
        // C36 — M1: analyze + zero-temp array/mutable forms + spread
        // statement lowering + residual gate (CE-1/2/3/5/6/7/12).
        new CollectionExprRule(),
    };
}
