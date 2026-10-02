using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 object/collection-initializer rule (fixed family order: second, after
/// Interpolations(C23) and before NullConditional(C35)/CollectionExpr(C36)).
///
/// M0 is deliberately transparent: <see cref="Analyze"/> only inventories
/// sites/kinds into <see cref="InitializerStatistics"/> (syntax level, no
/// semantic binding, no diagnostics) and the plan reports RewriteCount 0, so a
/// file containing initializer syntax is recognized as dirty but left
/// byte-identical (the family reports it as dirty-no-rewrite). There is no
/// residual gate in M0 — the real CS2J0545 gate is a M3 deliverable.
/// </summary>
internal sealed class InitializersRule : IExpressionDesugarRule
{
    public string Id => "Initializers";

    public bool IsPotentiallyDirty(CompilationUnitSyntax root)
        => InitializerSyntaxFacts.HasTriggerSyntax(root);

    public ExpressionDesugarRulePlan Analyze(RuleAnalysisContext context)
    {
        InitializerBindingAnalyzer.Analyze(context);
        return new InitializersRulePlan();
    }

    /// <summary>
    /// M0 analyze product: holds no plan data yet. Rewrite is a no-op (the
    /// input root is returned unchanged), no temps are pending and residual
    /// checking is deferred to M3.
    /// </summary>
    private sealed class InitializersRulePlan : ExpressionDesugarRulePlan
    {
        public override CompilationUnitSyntax Rewrite(CompilationUnitSyntax root) => root;

        public override IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp> PendingTemps
            => Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

        public override IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
            => EmptyAnnotations;

        public override int RewriteCount => 0;

        public override int CheckResiduals(
            RuleAnalysisContext context, CompilationUnitSyntax root)
            => 0; // M0: the real three-kind residual gate lands in M3.

        private static readonly IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation>
            EmptyAnnotations = new Dictionary<SyntaxNode, SyntaxAnnotation>();
    }
}