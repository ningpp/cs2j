using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GotoEliminator;

/// <summary>
/// Pass 1: 把 goto case X / goto default 降为普通标签 goto。
///
/// 在目标 section 开头合成 `__cs2jCaseLabel{index}_{case}` 标签，`goto case X` 改写为
/// `goto` 该标签。不得用 `{ state = N; break; }` 之类的"赋状态+break"模拟：section 内
/// 若有嵌套循环（e.g. IPv6AddressHelper 的 case '%' 内 while(true)），break 只会绑定到
/// 内层循环，控制流继续贯穿到下一个 case（CS0163）且外层状态机不会重新分发。
///
/// 标签 goto 由后续处理统一消除：switch 含 label/goto 时先经 LowerSwitchToBlock 降级，
/// 跨越嵌套循环跳到 section 级标签正是方法级状态机基本块划分的核心能力。
/// </summary>
internal sealed class GotoCaseDesugarer : CSharpSyntaxRewriter
{
    private int _switchIndex;

    public static CompilationUnitSyntax Run(CompilationUnitSyntax root)
        => (CompilationUnitSyntax)new GotoCaseDesugarer().Visit(root)!;

    // 注意：不得跳过 lambda/局部函数（无 overrides）——C# 中 `goto case` 只能出现在
    // switch 的词法范围内，lambda 内不可能有 goto case，无条件递归是安全的；
    // 而 RegexCompiler 等文件的 switch 位于局部函数内，跳过会漏掉整个方法。

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        // 先递归子节点（内部嵌套 switch 先处理——其 goto case 已被改写为普通标签 goto，
        // 因此本层收集到的 GotoCaseStatement 都属于本 switch）。
        var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;

        bool hasGotoCase = visited.DescendantNodes()
            .OfType<GotoStatementSyntax>()
            .Any(g => g.IsKind(SyntaxKind.GotoCaseStatement) || g.IsKind(SyntaxKind.GotoDefaultStatement));
        if (!hasGotoCase) return visited;

        int index = _switchIndex++;

        // case 值 → section 下标；default → section 下标。
        var sectionIndexByCase = new Dictionary<string, int>(StringComparer.Ordinal);
        int? defaultSectionIndex = null;
        for (int i = 0; i < visited.Sections.Count; i++)
        {
            foreach (var label in visited.Sections[i].Labels)
            {
                if (label is CaseSwitchLabelSyntax cs)
                    sectionIndexByCase.TryAdd(NormalizeCase(cs.Value), i);
                else if (label is DefaultSwitchLabelSyntax && defaultSectionIndex == null)
                    defaultSectionIndex = i;
            }
        }

        // 收集本 switch 实际被跳转的 case 值 / default。
        var targetedCases = new HashSet<string>(StringComparer.Ordinal);
        bool targetsDefault = false;
        foreach (var g in visited.DescendantNodes().OfType<GotoStatementSyntax>())
        {
            if (g.IsKind(SyntaxKind.GotoCaseStatement) && g.Expression != null)
                targetedCases.Add(NormalizeCase(g.Expression));
            else if (g.IsKind(SyntaxKind.GotoDefaultStatement))
                targetsDefault = true;
        }

        // 每个"被跳转的 section"分配一个标签（同一 section 的多个 case 值共享标签——
        // 跳到 section 体开头与经任一 label 进入等价，贯穿语义不变）。
        var labelByCase = new Dictionary<string, string>(StringComparer.Ordinal);
        var labelBySectionIndex = new Dictionary<int, string>();
        foreach (var normalized in targetedCases)
        {
            if (!sectionIndexByCase.TryGetValue(normalized, out var sectionIdx)) continue;
            if (!labelBySectionIndex.TryGetValue(sectionIdx, out var label))
            {
                label = $"__cs2jCaseLabel{index}_{normalized}";
                labelBySectionIndex[sectionIdx] = label;
            }
            labelByCase[normalized] = label;
        }
        if (targetsDefault && defaultSectionIndex is int defIdx)
        {
            labelBySectionIndex[defIdx] = $"__cs2jCaseLabel{index}_default";
        }

        if (labelBySectionIndex.Count == 0) return visited;

        // 改写各 section：goto case/default → goto 标签；目标 section 开头插入标签。
        var newSections = new List<SwitchSectionSyntax>();
        for (int i = 0; i < visited.Sections.Count; i++)
        {
            var section = visited.Sections[i];
            var rewriter = new SwitchSectionGotoRewriter(labelByCase, defaultLabel: labelBySectionIndex.TryGetValue(defaultSectionIndex ?? -1, out var dl) ? dl : null);
            var rewritten = (SwitchSectionSyntax)rewriter.Visit(section)!;

            if (labelBySectionIndex.TryGetValue(i, out var insertLabel))
            {
                var labeledEmpty = SyntaxFactory.LabeledStatement(
                    SyntaxFactory.Identifier(insertLabel),
                    SyntaxFactory.EmptyStatement());
                rewritten = rewritten.WithStatements(
                    SyntaxFactory.SingletonList<StatementSyntax>(labeledEmpty)
                        .AddRange(rewritten.Statements));
            }
            newSections.Add(rewritten);
        }

        return visited.WithSections(SyntaxFactory.List(newSections));
    }

    /// <summary>规范化 case 值：源文本去空白；字母/数字/下划线保留，其余字符按码点编码。</summary>
    internal static string NormalizeCase(ExpressionSyntax expr)
    {
        var text = expr.ToString().Replace(" ", "");
        return NormalizeCaseText(text);
    }

    /// <summary>
    /// Encodes case values so distinct labels never collide. Older normalization mapped
    /// every non-alphanumeric character (e.g. the char literals '\'', '&lt;', '(') to '_',
    /// so <c>goto case 'X'</c> in a switch with several punctuation labels (like
    /// RegexParser.ScanGroupOpen) jumped to the wrong section — the one registered last.
    /// Non-alphanumeric characters are now encoded as their code point (e.g. '(' → "_28_").
    /// </summary>
    private static string NormalizeCaseText(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : $"_{(int)ch:X}");
        return sb.ToString();
    }

    private sealed class SwitchSectionGotoRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _labelByCase;
        private readonly string? _defaultLabel;

        internal SwitchSectionGotoRewriter(Dictionary<string, string> labelByCase, string? defaultLabel)
        {
            _labelByCase = labelByCase;
            _defaultLabel = defaultLabel;
        }

        public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
        {
            // 嵌套 switch 已在外层递归中完成去糖，不进入（避免改写其普通 goto）。
            return node;
        }

        private static SyntaxToken IdentifierTokenWithLeadingSpace(string label)
            => SyntaxFactory.Identifier(
                SyntaxFactory.TriviaList(SyntaxFactory.Space), label, SyntaxFactory.TriviaList());

        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            if (node.IsKind(SyntaxKind.GotoCaseStatement) && node.Expression != null)
            {
                var normalized = NormalizeCase(node.Expression);
                if (_labelByCase.TryGetValue(normalized, out var label))
                {
                    return SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement,
                        SyntaxFactory.IdentifierName(IdentifierTokenWithLeadingSpace(label))).WithTriviaFrom(node);
                }
            }
            else if (node.IsKind(SyntaxKind.GotoDefaultStatement) && _defaultLabel != null)
            {
                return SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement,
                    SyntaxFactory.IdentifierName(IdentifierTokenWithLeadingSpace(_defaultLabel))).WithTriviaFrom(node);
            }
            return base.VisitGotoStatement(node);
        }
    }
}
