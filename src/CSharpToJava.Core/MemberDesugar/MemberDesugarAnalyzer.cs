using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>整树一次的共享分析入口（设计 §4.3-1）：成员索引 + SemanticModel 缓存。
/// 站点枚举/分类由各规则在 Analyze 中按需执行（C12-C14 只读复用本类）。</summary>
internal static class MemberDesugarAnalyzer
{
    public static (MemberDesugarAnalysisContext Context, IReadOnlyList<MemberDesugarPlan> Plans)
        Analyze(CSharpCompilation compilation, MemberDesugarOptions options)
    {
        var models = new List<SemanticModel>();
        var properties = new List<IPropertySymbol>();
        var seen = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!tree.TryGetRoot(out var root) || root is not CompilationUnitSyntax unit) continue;
            var model = compilation.GetSemanticModel(tree);
            models.Add(model);
            foreach (var decl in unit.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { } prop || prop.IsIndexer) continue;
                if (!seen.Add(prop)) continue; // partial 多部件按符号去重
                properties.Add(prop);
            }
        }

        var context = new MemberDesugarAnalysisContext
        {
            Compilation = compilation,
            Options = options,
            Models = models,
            SourceProperties = properties,
        };

        var builder = new MemberDesugarPlanBuilder();
        foreach (var rule in MemberDesugarRuleDiscovery.Discover())
            rule.Analyze(context, builder);
        return (context, builder.Build());
    }
}
