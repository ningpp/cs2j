using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.EnumDeclaration;

/// <summary>
/// Phase 2a：把 <see cref="EnumDeclarationSyntax"/> 整体替换为 Synthesizer 产出的
/// enum-like sealed class。使用点改写见 <see cref="EnumUseSiteRewriter"/>。
/// 声明节点替换与使用点不相交（合成成员初值由编译期常量重建，不保留 EqualsValue），
/// 因此两个 rewriter 可在同一棵树上串行执行。
/// </summary>
internal sealed class EnumDeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly HashSet<EnumDeclarationPlan> _allActive;

    public EnumDeclarationRewriter(IEnumerable<EnumDeclarationPlan> activePlans)
    {
        _allActive = new HashSet<EnumDeclarationPlan>(
            activePlans.Where(p => p.Feasible));
    }

    /// <summary>
    /// 逐文件应用：只替换该文件内声明的 active plan。使用点 rewriter 不改 enum
    /// 声明子树，故原始 <see cref="EnumDeclarationSyntax"/> 节点引用身份保持不变。
    /// 注意不能按 SyntaxTree 实例做键——Visit 产出的脱离根节点带有新的树包装。
    /// </summary>
    public SyntaxNode RewriteFileTree(SyntaxNode root, string filePath)
        => new DeclarationSwapRewriter(_allActive
            .Where(p => string.Equals(
                p.Declaration.SyntaxTree?.FilePath, filePath,
                StringComparison.OrdinalIgnoreCase))
            .ToHashSet()).Visit(root);

    /// <summary>
    /// 在任何文件改写之前，为全部可行 plan 物化合成声明（D16 命名表随之落到 plan.Names）。
    /// </summary>
    public static void PrepareDeclarations(IEnumerable<EnumDeclarationPlan> activePlans)
    {
        foreach (var plan in activePlans)
        {
            if (plan.Feasible && plan.Names == null)
                EnumDeclarationSynthesizer.Synthesize(plan);
        }
    }

    private sealed class DeclarationSwapRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<EnumDeclarationPlan> _plans;

        public DeclarationSwapRewriter(HashSet<EnumDeclarationPlan> plans) => _plans = plans;

        public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            // 使用点 rewriter 跳过了 enum 子树，结构必然等价；IsEquivalentTo 比引用
            // 身份更稳（脱离树重挂后节点实例标识不可依赖）。
            var plan = _plans.FirstOrDefault(p =>
                p.EnumShortName == node.Identifier.Text
                && p.Declaration.IsEquivalentTo(node));
            if (plan == null) return base.VisitEnumDeclaration(node);
            // Synthesize 确定性（同一 plan 多次调用产出相同 D16 命名与声明文本）。
            return EnumDeclarationSynthesizer.Synthesize(plan);
        }
    }
}
