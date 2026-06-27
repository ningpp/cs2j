using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GotoEliminator;

/// <summary>
/// Pass 2: 把含 goto/label 的方法体转为 while(true){switch(__state){...}} 状态机。
/// 本任务只实现「扁平化 + 基本块分割」。变量提升/发射在后续任务加。
/// </summary>
internal sealed partial class StateMachineBuilder : CSharpSyntaxRewriter
{
    public List<GotoEliminatorDiagnostic> Diagnostics { get; } = new();
    public int TransformedMethods { get; private set; }
    public int SkippedMethods { get; private set; }
    public int GotosEliminated { get; private set; }

    /// <summary>扁平化 + 基本块分割。输入：方法体顶层语句列表。</summary>
    internal static List<BasicBlock> SplitToBlocks(IEnumerable<StatementSyntax> statements)
    {
        var flat = Flatten(statements);
        var blocks = new List<BasicBlock>();
        var current = new BasicBlock { Index = 0 };
        blocks.Add(current);
        int blockIdx = 0;

        void NewBlock(string? label)
        {
            // 若当前块为空（无语句、无标签、未终结），复用它：直接设标签。
            // 这样新方法体首条语句是标签时，初始空块被复用而非被遗留。
            if (current.Statements.Count == 0
                && current.Label == null
                && current.Exit == BlockExit.FallThrough)
            {
                if (label != null)
                    current.Label = label;
                return;
            }
            // 否则开新块（无论上一块是否已终结；若已终结且后续为死代码，它们将被合并到旧块）
            blockIdx++;
            current = new BasicBlock { Index = blockIdx, Label = label };
            blocks.Add(current);
        }

        foreach (var stmt in flat)
        {
            // 标签：开新块并记标签（除非当前块可复用）
            if (stmt is LabeledStatementSyntax labeled)
            {
                NewBlock(labeled.Identifier.ValueText);
                // 标签后的实际语句：labeled.Statement（可能是空语句）
                if (!labeled.Statement.IsKind(SyntaxKind.EmptyStatement))
                    current.Statements.Add(labeled.Statement);
                continue;
            }

            current.Statements.Add(stmt);

            if (stmt is GotoStatementSyntax)
            {
                // 无条件跳转：标记 Exit，不开新块——后续语句（若有）作为死代码与 goto 同块。
                current.Exit = BlockExit.Goto;
            }
            else if (stmt is ReturnStatementSyntax || stmt is ThrowStatementSyntax)
            {
                current.Exit = BlockExit.Return;
            }
            else if (stmt is BreakStatementSyntax || stmt is ContinueStatementSyntax)
            {
                // 非 goto 的 break/continue（必在内层循环内）；作为块终结以便发射时不追加 fall-through。
                current.Exit = BlockExit.Break;
            }
            else if (ContainsTopLevelGoto(stmt))
            {
                // 条件跳转（if 包含 goto）：开新块以承载 fall-through 路径。
                current.Exit = BlockExit.ConditionalGoto;
                current = new BasicBlock { Index = ++blockIdx };
                blocks.Add(current);
            }
        }

        // 删除末尾空块（若为空且 FallThrough）
        if (blocks.Count > 1 && blocks[^1].Statements.Count == 0
            && blocks[^1].Label == null && blocks[^1].Exit == BlockExit.FallThrough)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        // 设置 FallThroughTarget
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Exit is BlockExit.FallThrough or BlockExit.ConditionalGoto)
            {
                blocks[i].FallThroughTarget = (i + 1 < blocks.Count) ? i + 1 : (int?)null;
            }
        }
        return blocks;
    }

    /// <summary>递归展开含 label/goto 的裸 BlockSyntax；不含的裸块作为单语句保留。</summary>
    private static List<StatementSyntax> Flatten(IEnumerable<StatementSyntax> statements)
    {
        var result = new List<StatementSyntax>();
        foreach (var s in statements)
        {
            if (s is BlockSyntax block && ContainsLabelOrGoto(block))
            {
                // 展开裸块
                result.AddRange(Flatten(block.Statements));
            }
            else
            {
                result.Add(s);
            }
        }
        return result;
    }

    private static bool ContainsLabelOrGoto(SyntaxNode node)
        => node.DescendantNodesAndSelf().Any(n => n is LabeledStatementSyntax or GotoStatementSyntax);

    /// <summary>语句自身（非后代控制体）是否含顶层 goto——用于 if/switch 包裹的 goto。</summary>
    private static bool ContainsTopLevelGoto(StatementSyntax stmt)
    {
        // if (c) goto L;  →  if 的直接子语句是 goto
        if (stmt is IfStatementSyntax iff)
        {
            if (IsOrContainsGotoDirect(iff.Statement)) return true;
            if (iff.Else != null && IsOrContainsGotoDirect(iff.Else.Statement)) return true;
        }
        return false;
    }

    private static bool IsOrContainsGotoDirect(StatementSyntax s)
    {
        if (s is GotoStatementSyntax) return true;
        if (s is BlockSyntax b)
            return b.Statements.Any(IsOrContainsGotoDirect);
        if (s is IfStatementSyntax iff)
            return IsOrContainsGotoDirect(iff.Statement)
                || (iff.Else != null && IsOrContainsGotoDirect(iff.Else.Statement));
        return false;
    }

    /// <summary>
    /// 提升「跨基本块使用」的局部声明到 while 循环之前：
    /// 循环外 `T x = default;`，原位置改为赋值 `x = expr;`。
    /// const 原地保留；using/fixed/ref 跨块抛 GotoEliminatorException（由调用方按 Strict 决定 warn-skip）。
    /// </summary>
    internal static List<StatementSyntax> HoistSpanningLocals(System.Collections.IList blocks)
    {
        var hoisted = new List<StatementSyntax>();
        var bbList = new List<BasicBlock>();
        foreach (var b in blocks) bbList.Add((BasicBlock)b);

        for (int i = 0; i < bbList.Count; i++)
        {
            var blockStmts = bbList[i].Statements;
            // 从后向前迭代：提升某声明（尤其无 initializer 时）会令块内语句列表收缩，
            // 若从前向后迭代会跳过紧随其后的下一条声明（XsdDuration.cs 中 `string errorCode;`
            // 后紧跟 `int length;` 即触发此 bug，导致 length 未被提升 → CS0165）。
            for (int s = blockStmts.Count - 1; s >= 0; s--)
            {
                if (blockStmts[s] is not LocalDeclarationStatementSyntax decl) continue;
                if (decl.Modifiers.Any(SyntaxKind.ConstKeyword)) continue;

                bool isUsing = decl.UsingKeyword.IsKind(SyntaxKind.UsingKeyword);
                bool isFixed = decl.Modifiers.Any(SyntaxKind.FixedKeyword)
                               || decl.Modifiers.Any(SyntaxKind.UnsafeKeyword);
                bool isRef = decl.Declaration.Variables
                    .Any(v => v.Initializer is null && decl.Modifiers.Any(SyntaxKind.RefKeyword));
                if (decl.Modifiers.Any(SyntaxKind.RefKeyword)
                    || decl.Modifiers.Any(SyntaxKind.OutKeyword))
                    isRef = true;

                var names = decl.Declaration.Variables.Select(v => v.Identifier.ValueText).ToList();
                bool spans = false;
                for (int j = i + 1; j < bbList.Count; j++)
                    if (bbList[j].Statements.Any(st => ReferencesAny(st, names))) { spans = true; break; }
                if (!spans) continue;

                if (isUsing) throw new GotoEliminatorException("spanning 'using' local not supported");
                if (isFixed) throw new GotoEliminatorException("spanning 'fixed/unsafe' local not supported");
                if (isRef) throw new GotoEliminatorException("spanning 'ref/out' local not supported");

                var type = decl.Declaration.Type;
                foreach (var v in decl.Declaration.Variables)
                {
                    var defaultDecl = SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(type,
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(v.Identifier)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                                        SyntaxFactory.DefaultExpression(type))))));
                    hoisted.Add(defaultDecl);
                }

                // 原位置：前缀语句 + 赋值（替代声明） + 后缀语句
                var assignStmts = new List<StatementSyntax>();
                for (int k = 0; k < s; k++) assignStmts.Add(blockStmts[k]);
                foreach (var v in decl.Declaration.Variables)
                    if (v.Initializer != null)
                    {
                        var assign = SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName(v.Identifier), v.Initializer.Value));
                        assignStmts.Add(assign);
                    }
                for (int k = s + 1; k < blockStmts.Count; k++) assignStmts.Add(blockStmts[k]);

                bbList[i].Statements.Clear();
                bbList[i].Statements.AddRange(assignStmts);
            }
        }
        return hoisted;
    }

    private static bool ReferencesAny(SyntaxNode node, IEnumerable<string> names)
    {
        var set = new HashSet<string>(names);
        return node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(id => set.Contains(id.Identifier.ValueText));
    }
}

/// <summary>Pass 2 发射部分：重写 dirty 方法为 while(true){switch(__state){...}}。</summary>
internal sealed partial class StateMachineBuilder
{
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (visited.Body == null) return visited; // 表达式体无 goto
        if (!ContainsLabelOrGoto(visited.Body)) return visited;

        try
        {
            var blocks = SplitToBlocks(visited.Body.Statements);
            var hoisted = HoistSpanningLocals(blocks);
            // out 参数在 while 循环前初始化为 default：状态机的 switch(__state) 破坏确定赋值分析，
            // 编译器无法证明所有 case 路径都赋过 out 参数 / 结构字段（XsdDuration.TryParse 的
            // out XsdDuration result + result._nanoseconds |= ... 即触发 CS0170/CS0177）。
            var prologue = BuildOutParameterInitializers(visited);
            prologue.AddRange(hoisted);
            var newBody = EmitStateMachine(blocks, prologue, visited.Body);
            TransformedMethods++;
            GotosEliminated += CountGotos(visited.Body);
            return visited.WithBody(newBody);
        }
        catch (GotoEliminatorException ex)
        {
            SkippedMethods++;
            Diagnostics.Add(new GotoEliminatorDiagnostic(
                GotoEliminatorSeverity.Warning, ex.Message,
                visited.Identifier.ValueText));
            return visited; // 原样保留该方法
        }
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        => RewriteBaseMethod(node, n => n.Body, (n, b) => n.WithBody(b));
    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        => RewriteBaseMethod(node, n => n.Body, (n, b) => n.WithBody(b));

    private SyntaxNode RewriteBaseMethod<T>(
        T node,
        Func<T, BlockSyntax?> getBody,
        Func<T, BlockSyntax, T> withBody) where T : BaseMethodDeclarationSyntax
    {
        // 不调用 base.Visit(node)——会经 Visit(SyntaxNode) 派发回 VisitConstructorDeclaration → 无限递归。
        // 方法体的实际改写由 SplitToBlocks + HoistSpanningLocals + EmitStateMachine 完成。
        var visited = node;
        var body = getBody(visited);
        if (body == null || !ContainsLabelOrGoto(body)) return visited;
        try
        {
            var blocks = SplitToBlocks(body.Statements);
            var hoisted = HoistSpanningLocals(blocks);
            var prologue = BuildOutParameterInitializers(visited);
            prologue.AddRange(hoisted);
            var newBody = EmitStateMachine(blocks, prologue, body);
            TransformedMethods++;
            GotosEliminated += CountGotos(body);
            return withBody(visited, newBody);
        }
        catch (GotoEliminatorException ex)
        {
            SkippedMethods++;
            Diagnostics.Add(new GotoEliminatorDiagnostic(
                GotoEliminatorSeverity.Warning, ex.Message));
            return visited;
        }
    }

    /// <summary>为方法的每个 out 参数生成 `param = default(T)!;` 初始化语句。</summary>
    private static List<StatementSyntax> BuildOutParameterInitializers(BaseMethodDeclarationSyntax method)
    {
        var result = new List<StatementSyntax>();
        if (method.ParameterList == null) return result;
        foreach (var p in method.ParameterList.Parameters)
        {
            if (!p.Modifiers.Any(SyntaxKind.OutKeyword)) continue;
            if (p.Type == null) continue;
            // default(T)! —— `!` 抑制可空引用类型的 null 警告；值类型上无害。
            var defaultExpr = SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                SyntaxFactory.DefaultExpression(p.Type));
            var assign = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(p.Identifier),
                    defaultExpr));
            result.Add(assign);
        }
        return result;
    }

    private static int CountGotos(SyntaxNode node)
        => node.DescendantNodes().OfType<GotoStatementSyntax>().Count();

    /// <summary>发射 while(true){switch(__state){case i: ...}} 并替换原方法体。</summary>
    internal static BlockSyntax EmitStateMachine(
        List<BasicBlock> blocks, List<StatementSyntax> hoisted, BlockSyntax originalBody)
    {
        // 标签 -> 块 index
        var labelToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
            if (blocks[i].Label != null) labelToIndex[blocks[i].Label!] = i;

        var sections = new List<SwitchSectionSyntax>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var caseStmts = new List<StatementSyntax>(block.Statements);
            // 改写块内 goto / conditional-goto；追加转移
            RewriteBlockStatements(caseStmts, block, labelToIndex, i, blocks);
            var label = SyntaxFactory.CaseSwitchLabel(
                SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                    SyntaxFactory.Literal(block.Index)));
            sections.Add(SyntaxFactory.SwitchSection(
                SyntaxFactory.SingletonList<SwitchLabelSyntax>(label),
                SyntaxFactory.List(caseStmts)));
        }

        // int __state = 0;
        var stateDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator("__state")
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                                SyntaxFactory.Literal(0)))))));

        // while (true) { switch (__state) { ... } }
        var whileStmt = SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(
                SyntaxFactory.SwitchStatement(SyntaxFactory.IdentifierName("__state"))
                    .WithSections(SyntaxFactory.List(sections))));

        var allStmts = new List<StatementSyntax>();
        allStmts.AddRange(hoisted);
        allStmts.Add(stateDecl);
        allStmts.Add(whileStmt);

        // 保留原方法体的 leading trivia（如文档注释）挂在第一个语句上
        if (allStmts.Count > 0 && originalBody.Statements.Count > 0)
        {
            var origLeading = originalBody.Statements[0].GetLeadingTrivia();
            allStmts[0] = allStmts[0].WithLeadingTrivia(origLeading);
        }
        return SyntaxFactory.Block(allStmts);
    }

    /// <summary>改写块内语句：goto label -> __state=N; continue; ；追加块转移。</summary>
    private static void RewriteBlockStatements(
        List<StatementSyntax> stmts, BasicBlock block,
        Dictionary<string, int> labelToIndex, int selfIndex,
        List<BasicBlock> blocks)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            stmts[i] = RewriteNode(stmts[i], labelToIndex);
        }

        switch (block.Exit)
        {
            case BlockExit.Goto:
                // 末语句是 goto；已被 RewriteNode 改写为 __state=N; continue;
                break;
            case BlockExit.ConditionalGoto:
                // if (c) goto L;  已被改写为 if (c){__state=L;continue;}
                // 追加 fall-through 转移
                if (block.FallThroughTarget is int ft)
                    stmts.Add(StateAssignContinue(ft));
                break;
            case BlockExit.FallThrough:
                if (block.FallThroughTarget is int ft2)
                    stmts.Add(StateAssignContinue(ft2));
                else
                    stmts.Add(StateAssignContinue(selfIndex)); // 末块自循环？不应发生；安全起见 break while
                break;
            case BlockExit.Return:
            case BlockExit.Break:
                // 块内已含 return/throw/break/continue，无需追加
                break;
        }
    }

    private static StatementSyntax RewriteNode(SyntaxNode node, Dictionary<string, int> labelToIndex)
    {
        var rewriter = new GotoTransitionRewriter(labelToIndex);
        return (StatementSyntax)rewriter.Visit(node)!;
    }

    private static StatementSyntax StateAssignContinue(int target)
        => SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName("__state"),
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(target)))),
            SyntaxFactory.ContinueStatement());

    /// <summary>把 goto &lt;identifier&gt; 改写为 { __state=N; continue; }。递归处理嵌套控制语句内的 goto。</summary>
    private sealed class GotoTransitionRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, int> _labelToIndex;
        internal GotoTransitionRewriter(Dictionary<string, int> labelToIndex) { _labelToIndex = labelToIndex; }
        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            // 此时只可能是 goto <identifier>（goto case/default 已去糖）
            if (node.Expression is IdentifierNameSyntax id
                && _labelToIndex.TryGetValue(id.Identifier.ValueText, out var idx))
            {
                return StateAssignContinue(idx).WithTriviaFrom(node);
            }
            return base.VisitGotoStatement(node);
        }
    }
}
