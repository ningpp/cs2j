using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
<<<<<<< HEAD
using CSharpToJava.Core.ExpressionDesugar.Interpolations;
=======
using CSharpToJava.Core.ExpressionDesugar.Initializers;
>>>>>>> 14a349c6 (feat(preprocessing): C24 initializers M0 探针+规则骨架+注册登记（默认关、空转）)

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Fixed in-family rule order (design §3.2):
/// <list type="number">
<<<<<<< HEAD
/// <item>Interpolations (C23) — landed; opt-in via
/// <see cref="Interpolations.InterpolationOptions.Enabled"/> (the family
/// itself is default-on for C36, so C23 stays inert until enabled);</item>
/// <item>Initializers (C24) — not yet merged, append position reserved;</item>
=======
/// <item>Interpolations (C23) — not yet merged, append position reserved;</item>
/// <item>Initializers (C24) — registered now (M0 transparent);</item>
>>>>>>> 14a349c6 (feat(preprocessing): C24 initializers M0 探针+规则骨架+注册登记（默认关、空转）)
/// <item>NullConditional (C35) — not yet merged, append position reserved;</item>
/// <item>CollectionExpr (C36) — registered as the M0 anchor.</item>
/// </list>
/// Rules run once per file in this order; append-only, never reorder.
/// </summary>
internal static class ExpressionDesugarRuleSet
{
    public static IReadOnlyList<IExpressionDesugarRule> Rules { get; } = new IExpressionDesugarRule[]
    {
<<<<<<< HEAD
        // C23 — IE-FOLD + IE-CONCAT + handler binding gate G0. Append first.
        new InterpolationRule(),
        // new InitializersRule(),     // C24 — append second
=======
        // new InterpolationsRule(),   // C23 — append first when it lands
        // C24 — M0: site inventory only; rewrite/context lowering/residual
        // gate land in M1–M3.
        new InitializersRule(),
>>>>>>> 14a349c6 (feat(preprocessing): C24 initializers M0 探针+规则骨架+注册登记（默认关、空转）)
        // new NullConditionalRule(),  // C35 — append third
        // C36 — M1: analyze + zero-temp array/mutable forms + spread
        // statement lowering + residual gate (CE-1/2/3/5/6/7/12).
        new CollectionExprRule(),
    };
}
