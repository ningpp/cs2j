using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.NumericBoundary;

namespace CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;

/// <summary>
/// C34 decimal-ops 规则（原 NumericBoundaryPreprocessor 内硬编码段的薄包装，
/// C17 设计 §4.5 2026-10-01 实施校订，Order=30）：DecimalOps 七个既有文件内部
/// 零改动；本类只保留原 L55-102 的执行序——重绑模型 → PlanConstDowngrades →
/// DecimalOpRewriter Visit/ApplyHoists → ConstDowngrader → 二次重绑与 DC-13
/// 残余闸门——以及 536x Error 向族 ErrorsByReason 的镜像。
/// <para>
/// 2026-10-16 性能等价改造（输出逐字节不变）：
/// <list type="bullet">
/// <item><b>复用已绑模型</b>：驱动方在每条改动规则之后都已重绑，C34 入口拿到的
/// <see cref="NumericBoundaryRuleContext.SemanticModel"/> 恒绑定当前
/// <see cref="NumericBoundaryRuleContext.Root"/>；原先无条件再重绑一次会把
/// Roslyn 的 GetOperation/GetSymbolInfo 缓存全部作废并重算整棵操作树。改为直接
/// 复用后语义结果不变，而 C22/C15 已绑好的操作缓存可被命中。</item>
/// <item><b>const 规划短路</b>：DC-12 只枚举 <c>const</c> 字段/局部声明符，无
/// <c>const</c> token 的文件计划集必为空，跳过两次全树遍历。</item>
/// <item><b>无锚空的降级遍历</b>：计划集为空时 ConstDowngrader 无标注可作用于
/// 是纯空转，跳过其全树 visit。</item>
/// <item><b>残余闸门短路</b>：DC-13 与 Pass1 分类同一候选集；本规则未改写、未新增
/// 任何诊断时不可能残留 Owned 站点，因此跳过二次重绑与残余扫描。</item>
/// </list>
/// </para>
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

        // Reuse the already-bound model whenever it is bound to the current root
        // (always true here: the driver re-binds after every tree-changing rule).
        // Re-binding would discard Roslyn's operation cache and recompute the
        // whole operation forest, while yielding the identical classifications.
        var postCheckedModel = ReferenceEquals(
                context.SemanticModel.SyntaxTree.GetRoot(), context.Root)
            ? context.SemanticModel
            : context.RebindModel(newRoot);
        if (postCheckedModel != null)
        {
            var reboundRoot = (CompilationUnitSyntax)postCheckedModel.SyntaxTree.GetRoot();

            // DC-12 planning only enumerates const field/local declarators; a file
            // without a const token has none, so the plan set is provably empty.
            var downgradeTargets = context.Profile.HasConstKeyword
                ? DecimalOpAnalyzer.PlanConstDowngrades(postCheckedModel, reboundRoot)
                : new HashSet<SyntaxNode>();

            var decimalRewriter = new DecimalOpRewriter(
                postCheckedModel, options, stats.DecimalOps, diagnostics, downgradeTargets);
            var diagnosticsBeforeSweep = diagnostics.Count;
            var decimalRoot = (CompilationUnitSyntax)decimalRewriter.Visit(reboundRoot)!;
            decimalRoot = (CompilationUnitSyntax)decimalRewriter.ApplyHoists(decimalRoot);

            decimalMadeChanges = decimalRewriter.MadeChanges;
            if (downgradeTargets.Count > 0)
            {
                var downgrader = new DecimalOpConstDowngrader(stats.DecimalOps);
                decimalRoot = (CompilationUnitSyntax)downgrader.Visit(decimalRoot)!;
                downgradeMadeChanges = downgrader.MadeChanges;
            }

            var noNewDiagnostics = diagnostics.Count == diagnosticsBeforeSweep;

            // DC-13: rescan the final output tree for surviving built-in decimal
            // operators. Every Owned candidate is either rewritten (MadeChanges)
            // or diagnosed (error) by the Pass1 sweep, so when nothing changed and
            // nothing was reported the gate has no residual to find and the whole
            // re-bind + re-scan is redundant.
            if (decimalMadeChanges || downgradeMadeChanges)
            {
                var finalModel = context.RebindModel(decimalRoot);
                if (finalModel != null)
                {
                    var finalRoot = (CompilationUnitSyntax)finalModel.SyntaxTree.GetRoot();
                    DecimalOpRewriter.ApplyResidualGate(
                        finalModel, finalRoot, options, stats.DecimalOps, diagnostics);
                }
            }
            else if (!noNewDiagnostics)
            {
                // Unchanged tree but new diagnostics: the residual gate runs on
                // the very same bound tree, so no re-bind is needed.
                DecimalOpRewriter.ApplyResidualGate(
                    postCheckedModel, reboundRoot, options, stats.DecimalOps, diagnostics);
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
