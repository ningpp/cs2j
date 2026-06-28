using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GotoEliminator;

/// <summary>
/// Pass 1: 把 goto case X / goto default 降为局部 switch-state 循环。
/// 输出不含 goto/label；后续方法级状态机只需要处理普通 goto label。
/// </summary>
internal sealed class GotoCaseDesugarer : CSharpSyntaxRewriter
{
    private int _switchIndex;
    private int _loopDepth;

    public static CompilationUnitSyntax Run(CompilationUnitSyntax root)
        => (CompilationUnitSyntax)new GotoCaseDesugarer().Visit(root)!;

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        _loopDepth++;
        var result = base.VisitForStatement(node);
        _loopDepth--;
        return result;
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        _loopDepth++;
        var result = base.VisitForEachStatement(node);
        _loopDepth--;
        return result;
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        _loopDepth++;
        var result = base.VisitWhileStatement(node);
        _loopDepth--;
        return result;
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        _loopDepth++;
        var result = base.VisitDoStatement(node);
        _loopDepth--;
        return result;
    }

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        // 先递归子节点（内部嵌套 switch 先处理）
        var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;

        bool hasGotoCase = visited.DescendantNodes()
            .OfType<GotoStatementSyntax>()
            .Any(g => g.IsKind(SyntaxKind.GotoCaseStatement) || g.IsKind(SyntaxKind.GotoDefaultStatement));
        if (!hasGotoCase) return visited;

        int n = _switchIndex++;
        var stateName = "__cs2jSwitchState" + n;
        var continueName = "__cs2jSwitchContinue" + n;

        // 收集本 switch 内所有 goto case/default 的目标。
        var targetedCaseNames = new HashSet<string>();
        var targetsDefault = false;
        foreach (var g in visited.DescendantNodes().OfType<GotoStatementSyntax>())
        {
            if (g.IsKind(SyntaxKind.GotoCaseStatement) && g.Expression != null)
                targetedCaseNames.Add(NormalizeCase(g.Expression));
            else if (g.IsKind(SyntaxKind.GotoDefaultStatement))
                targetsDefault = true;
        }

        var targetStateByCase = new Dictionary<string, int>(StringComparer.Ordinal);
        int? defaultState = null;
        var targetSections = new List<(int State, SwitchSectionSyntax Section)>();
        var nextState = 1;
        foreach (var section in visited.Sections)
        {
            var sectionState = (int?)null;
            foreach (var label in section.Labels)
            {
                if (label is CasePatternSwitchLabelSyntax cp)
                {
                    var normalized = NormalizeCase(cp.Pattern);
                    if (targetedCaseNames.Contains(normalized))
                    {
                        sectionState ??= nextState++;
                        targetStateByCase[normalized] = sectionState.Value;
                    }
                }
                else if (label is CaseSwitchLabelSyntax cs)
                {
                    var normalized = NormalizeCase(cs.Value);
                    if (targetedCaseNames.Contains(normalized))
                    {
                        sectionState ??= nextState++;
                        targetStateByCase[normalized] = sectionState.Value;
                    }
                }
                else if (label is DefaultSwitchLabelSyntax && targetsDefault)
                {
                    sectionState ??= nextState++;
                    defaultState = sectionState.Value;
                }
            }

            if (sectionState is int state)
                targetSections.Add((state, section));
        }

        var initialRewriter = new SwitchSectionRewriter(
            stateName, continueName, targetStateByCase, defaultState, _loopDepth);
        var initialSections = visited.Sections
            .Select(section => section.WithStatements(RewriteStatements(section, initialRewriter)))
            .ToList();
        var initialSwitch = visited.WithSections(SyntaxFactory.List(initialSections));

        var stateSections = new List<SwitchSectionSyntax>();
        stateSections.Add(ScopedStateSection(
            0,
            new StatementSyntax[]
            {
                initialSwitch,
                SetStateToExitIfStillInitial(stateName),
                SyntaxFactory.BreakStatement(),
            }));

        var usesOuterContinue = initialRewriter.UsesOuterContinue;
        foreach (var (state, section) in targetSections)
        {
            var rewriter = new SwitchSectionRewriter(
                stateName, continueName, targetStateByCase, defaultState, _loopDepth);
            var rewrittenStatements = RewriteStatements(section, rewriter).ToList();
            rewrittenStatements.Add(SyntaxFactory.BreakStatement());
            usesOuterContinue |= rewriter.UsesOuterContinue;
            stateSections.Add(ScopedStateSection(state, rewrittenStatements));
        }

        var declarations = new List<StatementSyntax>
        {
            SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(stateName)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(Number(0)))))),
        };
        if (usesOuterContinue)
        {
            declarations.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(continueName)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)))))));
        }

        declarations.Add(SyntaxFactory.WhileStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.GreaterThanOrEqualExpression,
                SyntaxFactory.IdentifierName(stateName),
                Number(0)),
            SyntaxFactory.Block(
                SyntaxFactory.SwitchStatement(SyntaxFactory.IdentifierName(stateName))
                    .WithSections(SyntaxFactory.List(stateSections)))));

        if (usesOuterContinue)
        {
            declarations.Add(SyntaxFactory.IfStatement(
                SyntaxFactory.IdentifierName(continueName),
                SyntaxFactory.ContinueStatement()));
        }

        if (!SwitchMayCompleteNormally(visited))
            declarations.Add(TerminalThrow());

        return SyntaxFactory.Block(declarations).WithTriviaFrom(node);
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

    private static SyntaxList<StatementSyntax> RewriteStatements(
        SwitchSectionSyntax section,
        SwitchSectionRewriter rewriter)
        => SyntaxFactory.List(section.Statements.Select(s => (StatementSyntax)rewriter.Visit(s)!));

    private static LiteralExpressionSyntax Number(int value)
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(value));

    private static StatementSyntax AssignState(string stateName, int value)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(stateName),
                Number(value)));

    private static StatementSyntax SetStateToExitIfStillInitial(string stateName)
        => SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                SyntaxFactory.IdentifierName(stateName),
                Number(0)),
            AssignState(stateName, -1));

    private static StatementSyntax AssignBool(string name, bool value)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(name),
                SyntaxFactory.LiteralExpression(value
                    ? SyntaxKind.TrueLiteralExpression
                    : SyntaxKind.FalseLiteralExpression)));

    private static StatementSyntax StateAssignBreak(string stateName, int state)
        => SyntaxFactory.Block(
            AssignState(stateName, state),
            SyntaxFactory.BreakStatement());

    private static StatementSyntax StateAssignContinueOuter(string stateName, string continueName)
        => SyntaxFactory.Block(
            AssignState(stateName, -1),
            AssignBool(continueName, true),
            SyntaxFactory.BreakStatement());

    private static SwitchSectionSyntax ScopedStateSection(
        int state,
        IEnumerable<StatementSyntax> statements)
        => SyntaxFactory.SwitchSection(
            SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.CaseSwitchLabel(Number(state))),
            SyntaxFactory.List<StatementSyntax>(new StatementSyntax[]
            {
                SyntaxFactory.Block(statements),
                SyntaxFactory.BreakStatement(),
            }));

    private static StatementSyntax TerminalThrow()
        => SyntaxFactory.ThrowStatement(
            SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("global::System.InvalidOperationException"))
                .WithArgumentList(SyntaxFactory.ArgumentList()));

    private static bool SwitchMayCompleteNormally(SwitchStatementSyntax node)
    {
        var hasDefault = node.Sections
            .SelectMany(s => s.Labels)
            .Any(l => l is DefaultSwitchLabelSyntax);
        if (!hasDefault)
            return true;

        return node.Sections.Any(SectionMayCompleteNormally);
    }

    private static bool SectionMayCompleteNormally(SwitchSectionSyntax section)
        => StatementsMayCompleteNormally(section.Statements);

    private static bool StatementsMayCompleteNormally(IEnumerable<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            if (!StatementMayCompleteNormally(statement))
                return false;
        }

        return true;
    }

    private static bool StatementMayCompleteNormally(StatementSyntax statement)
    {
        switch (statement)
        {
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case ContinueStatementSyntax:
            case GotoStatementSyntax:
                return false;
            case BlockSyntax block:
                return StatementsMayCompleteNormally(block.Statements);
            case IfStatementSyntax ifStatement:
                return IfMayCompleteNormally(ifStatement);
            case SwitchStatementSyntax switchStatement:
                return SwitchMayCompleteNormally(switchStatement);
            default:
                return true;
        }
    }

    private static bool IfMayCompleteNormally(IfStatementSyntax ifStatement)
    {
        if (ifStatement.Else == null)
            return true;

        return StatementMayCompleteNormally(ifStatement.Statement)
            || StatementMayCompleteNormally(ifStatement.Else.Statement);
    }

    private sealed class SwitchSectionRewriter : CSharpSyntaxRewriter
    {
        private readonly string _stateName;
        private readonly string _continueName;
        private readonly Dictionary<string, int> _targetStateByCase;
        private readonly int? _defaultState;
        private readonly int _outerLoopDepth;
        private int _breakableDepth;
        private int _continuableDepth;

        internal bool UsesOuterContinue { get; private set; }

        internal SwitchSectionRewriter(
            string stateName,
            string continueName,
            Dictionary<string, int> targetStateByCase,
            int? defaultState,
            int outerLoopDepth)
        {
            _stateName = stateName;
            _continueName = continueName;
            _targetStateByCase = targetStateByCase;
            _defaultState = defaultState;
            _outerLoopDepth = outerLoopDepth;
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

        public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
        {
            _breakableDepth++;
            var result = base.VisitSwitchStatement(node);
            _breakableDepth--;
            return result;
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            _breakableDepth++;
            _continuableDepth++;
            var result = base.VisitForStatement(node);
            _continuableDepth--;
            _breakableDepth--;
            return result;
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            _breakableDepth++;
            _continuableDepth++;
            var result = base.VisitForEachStatement(node);
            _continuableDepth--;
            _breakableDepth--;
            return result;
        }

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            _breakableDepth++;
            _continuableDepth++;
            var result = base.VisitWhileStatement(node);
            _continuableDepth--;
            _breakableDepth--;
            return result;
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            _breakableDepth++;
            _continuableDepth++;
            var result = base.VisitDoStatement(node);
            _continuableDepth--;
            _breakableDepth--;
            return result;
        }

        public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node)
        {
            if (_breakableDepth == 0)
                return StateAssignBreak(_stateName, -1).WithTriviaFrom(node);
            return node;
        }

        public override SyntaxNode? VisitContinueStatement(ContinueStatementSyntax node)
        {
            if (_continuableDepth == 0 && _outerLoopDepth > 0)
            {
                UsesOuterContinue = true;
                return StateAssignContinueOuter(_stateName, _continueName).WithTriviaFrom(node);
            }
            return node;
        }

        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            if (node.IsKind(SyntaxKind.GotoCaseStatement) && node.Expression != null)
            {
                var normalized = NormalizeCase(node.Expression);
                if (_targetStateByCase.TryGetValue(normalized, out var state))
                    return StateAssignBreak(_stateName, state).WithTriviaFrom(node);
            }
            else if (node.IsKind(SyntaxKind.GotoDefaultStatement) && _defaultState is int defaultState)
            {
                return StateAssignBreak(_stateName, defaultState).WithTriviaFrom(node);
            }
            return base.VisitGotoStatement(node);
        }
    }
}
