using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Fixed in-family rule order (design §3.2):
/// <list type="number">
/// <item>Interpolations (C23) — not yet merged, append position reserved;</item>
/// <item>Initializers (C24) — not yet merged, append position reserved;</item>
/// <item>NullConditional (C35) — not yet merged, append position reserved;</item>
/// <item>CollectionExpr (C36) — registered now as the M0 anchor.</item>
/// </list>
/// Rules run once per file in this order; append-only, never reorder.
/// </summary>
internal static class ExpressionDesugarRuleSet
{
    public static IReadOnlyList<IExpressionDesugarRule> Rules { get; } = new IExpressionDesugarRule[]
    {
        // new InterpolationsRule(),   // C23 — append first when it lands
        // new InitializersRule(),     // C24 — append second
        // new NullConditionalRule(),  // C35 — append third
        // C36 — M1: analyze + zero-temp array/mutable forms + spread
        // statement lowering + residual gate (CE-1/2/3/5/6/7/12).
        new CollectionExprRule(),
    };
}
