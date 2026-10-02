using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.NumericBoundary;

namespace CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;

/// <summary>
/// C34 decimal-ops 规则（原 NumericBoundaryPreprocessor 内硬编码段的薄包装，
/// C17 设计 §4.5 2026-10-01 实施校订，Order=30）：DecimalOps 七个既有文件内部
/// 零改动；本类只保留原 L55-102 的执行序——重绑模型 → PlanConstDowngrades →
/// DecimalOpRewriter Visit/ApplyHoists → ConstDowngrader → 二次重绑与 DC-13
/// 残余闸门——以及 536x Error 向族 ErrorsByReason 的镜像。
/// </summary>
internal sealed class DecimalOpsRule : INumericBoundaryRule
{
    public string Id => "decimal-ops";

    public int Order => 30;

    public bool IsEnabled(NumericBoundaryOptions options) => options.EnableDecimalOps;

    public NumericBoundaryRuleResult Execute(NumericBoundaryRuleContext context)
    {
        var options = context.Options;
        var stats = context.Stats;
        var diagnostics = context.Diagnostics;
        var newRoot = context.Root;

        var decimalMadeChanges = false;
        var downgradeMadeChanges = false;

        // Semantic-model guard (dormant on parse-only inputs): without a bound
        // compilation C34 cannot classify operator bindings.
        if (context.SemanticModel == null)
        {
            return new NumericBoundaryRuleResult(newRoot, false);
        }

        // C15 produced a new tree: rebuild a semantic model before C34 can
        // classify operator bindings on the post-hoist intermediate form.
        // The model is bound to a freshly parsed tree, so every subsequent
        // operation uses that tree's own root.
        var postCheckedModel = context.RebindModel(newRoot);
        if (postCheckedModel != null)
        {
            var reboundRoot = (CompilationUnitSyntax)postCheckedModel.SyntaxTree.GetRoot();
            var downgradeTargets = DecimalOpAnalyzer.PlanConstDowngrades(postCheckedModel, reboundRoot);

            var decimalRewriter = new DecimalOpRewriter(
                postCheckedModel, options, stats.DecimalOps, diagnostics, downgradeTargets);
            var decimalRoot = (CompilationUnitSyntax)decimalRewriter.Visit(reboundRoot)!;
            decimalRoot = (CompilationUnitSyntax)decimalRewriter.ApplyHoists(decimalRoot);

            var downgrader = new DecimalOpConstDowngrader(stats.DecimalOps);
            decimalRoot = (CompilationUnitSyntax)downgrader.Visit(decimalRoot)!;
            decimalMadeChanges = decimalRewriter.MadeChanges;
            downgradeMadeChanges = downgrader.MadeChanges;

            // DC-13: rescan the final output tree for surviving built-in
            // decimal operators (lifted/user-operator sites whitelisted).
            var finalModel = context.RebindModel(decimalRoot);
            if (finalModel != null)
            {
                var finalRoot = (CompilationUnitSyntax)finalModel.SyntaxTree.GetRoot();
                DecimalOpRewriter.ApplyResidualGate(
                    finalModel, finalRoot, options, stats.DecimalOps, diagnostics);
            }

            newRoot = decimalRoot;
        }

        // Mirror C34 error codes into the C15 error buckets so adapters that only
        // merge ErrorsByReason still see them.
        foreach (var errorCode in diagnostics
                     .Where(d => d.Severity == NumericBoundarySeverity.Error && d.Code.StartsWith("CS2J536", StringComparison.Ordinal))
                     .Select(d => d.Code))
        {
            stats.ErrorsByReason[errorCode] = stats.ErrorsByReason.GetValueOrDefault(errorCode) + 1;
        }

        return new NumericBoundaryRuleResult(newRoot, decimalMadeChanges || downgradeMadeChanges);
    }
}
