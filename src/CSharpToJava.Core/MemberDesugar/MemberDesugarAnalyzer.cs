using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>整树一次的共享分析入口（设计 §4.3-1）：成员索引 + SemanticModel 缓存。
/// 站点枚举/分类由各规则在 Analyze 中按需执行（C12-C14 只读复用本类）。
///
/// 与历史串行版本【逐字节等价】，仅去掉串行等待：树遍历按 tree 并行；规则族按 family
/// 并行（各 family 写各自 builder）。结果按 tree 序 / 规则发现序稳定合并，plan 集合最终
/// 仍由 <see cref="MemberDesugarPlanBuilder.Build"/> 按 Id 排序，去重（partial 多部件）与
/// 首现顺序均与串行一致。<c>C46</c> 审计规则（建立 <see cref="MemberDesugarAnalysisContext.SignatureAnalysis"/>）
/// 先串行执行，因为 SI/NH/CR 只读复用它；<c>OperatorRule</c> 独占写
/// <see cref="MemberDesugarAnalysisContext.MetadataOperatorSitesByFile"/>，只有它在本族线程写该表。</summary>
internal static class MemberDesugarAnalyzer
{
    public static (MemberDesugarAnalysisContext Context, IReadOnlyList<MemberDesugarPlan> Plans)
        Analyze(CSharpCompilation compilation, MemberDesugarOptions options)
    {
        var trees = compilation.SyntaxTrees as IReadOnlyList<SyntaxTree>
            ?? compilation.SyntaxTrees.ToList();
        var modelSlots = new SemanticModel?[trees.Count];
        var propertySlots = new List<IPropertySymbol>?[trees.Count];

        // 逐树并行：模型获取 + 属性声明符号收集（语义绑定是这里最贵的部分）。
        Parallel.For(0, trees.Count, i =>
        {
            var tree = trees[i];
            if (!tree.TryGetRoot(out var root) || root is not CompilationUnitSyntax unit) return;
            var model = compilation.GetSemanticModel(tree);
            modelSlots[i] = model;
            var list = new List<IPropertySymbol>();
            foreach (var decl in unit.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { } prop || prop.IsIndexer) continue;
                list.Add(prop);
            }

            propertySlots[i] = list;
        });

        var models = new List<SemanticModel>();
        var properties = new List<IPropertySymbol>();
        var seen = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        for (var i = 0; i < trees.Count; i++)
        {
            if (modelSlots[i] is { } model) models.Add(model);
            if (propertySlots[i] is { } slot)
            {
                foreach (var prop in slot)
                {
                    if (!seen.Add(prop)) continue; // partial 多部件按符号去重（保持首现顺序）
                    properties.Add(prop);
                }
            }
        }

        var context = new MemberDesugarAnalysisContext
        {
            Compilation = compilation,
            Options = options,
            Models = models,
            SourceProperties = properties,
        };

        var rules = MemberDesugarRuleDiscovery.Discover();
        var builders = new MemberDesugarPlanBuilder[rules.Count];

        // 审计规则先行（Kind=Signature, Order=30）：不建/不扫时为零执行空跑。
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i] is not Signatures.SignatureAuditRule) continue;
            builders[i] = new MemberDesugarPlanBuilder();
            rules[i].Analyze(context, builders[i]);
            break;
        }

        // 其余规则族并行（各写各自 builder；仅 OperatorRule 写其独占的
        // MetadataOperatorSitesByFile，单线程写故安全）。
        Parallel.For(0, rules.Count, i =>
        {
            if (builders[i] != null) return;
            var builder = new MemberDesugarPlanBuilder();
            rules[i].Analyze(context, builder);
            builders[i] = builder;
        });

        var merged = new MemberDesugarPlanBuilder();
        foreach (var builder in builders)
        {
            foreach (var plan in builder.Build())
            {
                merged.Add(plan);
            }
        }

        return (context, merged.Build());
    }
}
