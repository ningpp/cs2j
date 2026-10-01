using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;

/// <summary>
/// C17 IncrementTargets family rule (Order=10 — runs before the C15 checked
/// sweep so expanded arithmetic nodes are seen by C15, design decision 17 /
/// §4.5 2026-10-01 implementation correction).
/// <para>
/// M0 骨架：仅在真实文件树上跑一遍只读 <see cref="IncrementTargetAnalyzer"/>
/// （验证四元组分类器不抛异常），不写树、不发诊断、统计桶不自增，恒返回
/// MadeChanges=false。M1 在此接入 Pass1（站点读-改-写替换）与 Pass2
/// （锚点临时提升），M2 接入特殊算术核与 CS2J5332-5341 诊断。
/// </para>
/// </summary>
internal sealed class IncrementTargetRule : INumericBoundaryRule
{
    public string Id => "increment-targets";

    public int Order => 10;

    public bool IsEnabled(NumericBoundaryOptions options) => options.EnableIncrementTargets;

    public NumericBoundaryRuleResult Execute(NumericBoundaryRuleContext context)
    {
        // Parse-only inputs carry no semantic model: classification is entirely
        // semantic-model bound, so the rule is a no-op there.
        if (context.SemanticModel == null)
        {
            return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
        }

        var analyzer = new IncrementTargetAnalyzer(
            context.SemanticModel, context.Options, context.GetSharedAnalyzer());

        // M0: exercise the classifier over the real tree only; results are
        // discarded (no tree writes, no diagnostics, no statistics moves).
        _ = analyzer.FindAllSites(context.Root);

        return new NumericBoundaryRuleResult(context.Root, MadeChanges: false);
    }
}
