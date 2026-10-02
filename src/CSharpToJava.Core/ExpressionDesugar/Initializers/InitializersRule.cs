using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 object/collection-initializer rule (fixed family order: second, after
/// Interpolations(C23) and before NullConditional(C35)/CollectionExpr(C36)).
///
/// <para>
/// Rollout gate (design §9 "M0–M4 全程族 pass DefaultEnabled=false", plan §0-1
/// "默认关"): the rule stays inert unless the host opts in by providing the
/// <see cref="ExpressionDesugarOptions.Initializers"/> subsection. The
/// expression-desugar pass itself is default-on, so an always-active C24 would
/// immediately rewrite/repair-reject the object initializers of every default
/// chain project (e.g. init-only members without member-desugar, CS2J0531).
/// With the subsection absent the rule reproduces its M0 behaviour exactly:
/// initializer syntax is still recognized as dirty but left byte-identical.
/// </para>
///
/// Analyze runs the IOperation-driven classification
/// (<see cref="InitializerBindingAnalyzer"/>) producing per-site plans,
/// diagnostics and pre-allocated temp names; Rewrite runs one bottom-up
/// <see cref="InitializerRewriter"/> that hands statement sequences to the
/// family TempLifter. Strict Error diagnostics (0531/0533/0535) abort the whole
/// file atomically in the family entry; non-strict downgrades them to Warnings
/// and the affected sites are preserved while siblings rewrite. M2 shapes and
/// M2-only contexts are preserved silently (design §3 M1 scope); the residual
/// gate lands in M3.
/// </summary>
internal sealed class InitializersRule : IExpressionDesugarRule
{
    public string Id => "Initializers";

    public bool IsPotentiallyDirty(CompilationUnitSyntax root)
        => InitializerSyntaxFacts.HasTriggerSyntax(root);

    public ExpressionDesugarRulePlan Analyze(RuleAnalysisContext context)
    {
        // Rollout gate: the C24 subsection must be present (see class remarks).
        if (context.Options.Initializers is null)
            return new InitializersRulePlan(Array.Empty<InitializerPlan>());

        var plans = InitializerBindingAnalyzer.Analyze(context);
        return new InitializersRulePlan(plans);
    }

    /// <summary>
    /// Analyze product: owns the site plans and runs the bottom-up rewriter.
    /// Sites deferred for C36 coexistence (nested inside a collection
    /// expression) stay out of the rewriter map, so no ancestor is rebuilt and
    /// C36's original-node-keyed plans keep resolving. Residual checking is
    /// deferred to M3 (the three-kind gate needs the M2 skip pairing); M1
    /// returns 0 so the family gate stays truthful.
    /// </summary>
    private sealed class InitializersRulePlan : ExpressionDesugarRulePlan
    {
        private readonly IReadOnlyList<InitializerPlan> _plans;
        private InitializerRewriter? _rewriter;

        public InitializersRulePlan(IReadOnlyList<InitializerPlan> plans)
        {
            _plans = plans;
        }

        public override CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
        {
            var byNode = new Dictionary<BaseObjectCreationExpressionSyntax, InitializerPlan>();
            foreach (var plan in _plans)
            {
                if (plan.Outcome == InitializerOutcome.DeferredForCoexistence)
                    continue;

                byNode[plan.Node] = plan;
            }

            _rewriter = new InitializerRewriter(byNode);
            return (CompilationUnitSyntax)_rewriter.Visit(root)!;
        }

        public override IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp> PendingTemps
            => (IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp>?)_rewriter?.Pending
               ?? Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

        public override IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
            => _rewriter?.AnchorAnnotations
               ?? new Dictionary<SyntaxNode, SyntaxAnnotation>();

        public override int RewriteCount => _rewriter?.RewriteCount ?? 0;

        public override int CheckResiduals(
            RuleAnalysisContext context, CompilationUnitSyntax root)
            => 0; // M3: the three-kind residual gate lands there.
    }
}
