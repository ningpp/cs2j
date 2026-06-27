using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GotoEliminator;

/// <summary>
/// Pass 1: 把 goto case X / goto default 去糖为 goto __caseN_X / goto __defaultN，
/// 并在被引用的 switch section 顶部插入合成标签。去糖后只剩 goto &lt;identifier&gt;。
/// </summary>
internal sealed class GotoCaseDesugarer : CSharpSyntaxRewriter
{
    private int _switchIndex;

    public static CompilationUnitSyntax Run(CompilationUnitSyntax root)
        => (CompilationUnitSyntax)new GotoCaseDesugarer().Visit(root)!;

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        // 先递归子节点（内部嵌套 switch 先处理）
        var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;

        bool hasGotoCase = visited.DescendantNodes()
            .OfType<GotoStatementSyntax>()
            .Any(g => g.IsKind(SyntaxKind.GotoCaseStatement) || g.IsKind(SyntaxKind.GotoDefaultStatement));
        if (!hasGotoCase) return visited;

        int n = _switchIndex++;
        // 收集本 switch 内所有 goto case 的目标规范化名
        var targetedCaseNames = new HashSet<string>();
        var targetsDefault = false;
        foreach (var g in visited.DescendantNodes().OfType<GotoStatementSyntax>())
        {
            if (g.IsKind(SyntaxKind.GotoCaseStatement) && g.Expression != null)
                targetedCaseNames.Add(NormalizeCase(g.Expression));
            else if (g.IsKind(SyntaxKind.GotoDefaultStatement))
                targetsDefault = true;
        }

        // 重写每个 section：在顶部插入命中目标的合成标签
        var newSections = new List<SwitchSectionSyntax>();
        foreach (var section in visited.Sections)
        {
            var labels = section.Labels;
            var stmts = section.Statements.ToList();
            foreach (var label in labels)
            {
                string? synthetic = null;
                if (label is CasePatternSwitchLabelSyntax cp)
                    synthetic = MaybeSynthetic(NormalizeCase(cp.Pattern), targetedCaseNames, n, isDefault: false);
                else if (label is CaseSwitchLabelSyntax cs)
                    synthetic = MaybeSynthetic(NormalizeCase(cs.Value), targetedCaseNames, n, isDefault: false);
                else if (label is DefaultSwitchLabelSyntax && targetsDefault)
                    synthetic = "__default" + n;
                if (synthetic != null)
                {
                    // 合成标签：LabeledStatement 包裹一个空语句，置于 section 语句最前
                    var empty = SyntaxFactory.EmptyStatement();
                    var labeled = SyntaxFactory.LabeledStatement(synthetic, empty)
                        .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
                    stmts.Insert(0, labeled);
                }
            }
            newSections.Add(section.WithStatements(SyntaxFactory.List(stmts)));
        }

        var withSections = visited.WithSections(SyntaxFactory.List(newSections));

        // 重写 goto case / goto default
        var rewritten = (SwitchStatementSyntax)new GotoRewriter(n).Visit(withSections)!;
        return rewritten;
    }

    private static string? MaybeSynthetic(string normalized, HashSet<string> targets, int n, bool isDefault)
    {
        if (isDefault) return "__default" + n;
        return targets.Contains(normalized) ? "__case" + n + "_" + normalized : null;
    }

    /// <summary>规范化 case 值：源文本去空白，非字母数字→下划线。</summary>
    internal static string NormalizeCase(ExpressionSyntax expr)
    {
        var text = expr.ToString().Replace(" ", "");
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return sb.ToString();
    }

    /// <summary>规范化 case 模式：取模式文本（如常量模式 2 → "2"）。</summary>
    private static string NormalizeCase(SyntaxNode pattern)
    {
        // CasePatternSwitchLabel 的 Pattern 可能是 ConstantPatternSyntax 等。
        // 简化处理：取其第一个 ExpressionSyntax 后代，回退到模式文本。
        var expr = pattern.DescendantNodes().OfType<ExpressionSyntax>().FirstOrDefault();
        var text = (expr ?? pattern).ToString().Replace(" ", "");
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return sb.ToString();
    }

    private sealed class GotoRewriter : CSharpSyntaxRewriter
    {
        private readonly int _n;
        internal GotoRewriter(int n) { _n = n; }
        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            if (node.IsKind(SyntaxKind.GotoCaseStatement) && node.Expression != null)
            {
                var name = "__case" + _n + "_" + NormalizeCase(node.Expression);
                var id = SyntaxFactory.IdentifierName(name)
                    .WithLeadingTrivia(SyntaxFactory.Space);
                return SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement, id)
                    .WithTriviaFrom(node);
            }
            if (node.IsKind(SyntaxKind.GotoDefaultStatement))
            {
                var name = "__default" + _n;
                var id = SyntaxFactory.IdentifierName(name)
                    .WithLeadingTrivia(SyntaxFactory.Space);
                return SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement, id)
                    .WithTriviaFrom(node);
            }
            return base.VisitGotoStatement(node);
        }
    }
}
