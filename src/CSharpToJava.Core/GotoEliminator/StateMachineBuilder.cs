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

        // 设置当前块 Exit；ConditionalGoto 时开新块承载 fall-through 路径。
        void ApplyExit(BasicBlock blk, StatementSyntax s)
        {
            if (s is GotoStatementSyntax)
            {
                // 无条件跳转：标记 Exit，不开新块——后续语句（若有）作为死代码与 goto 同块。
                blk.Exit = BlockExit.Goto;
            }
            else if (s is ReturnStatementSyntax || s is ThrowStatementSyntax)
            {
                blk.Exit = BlockExit.Return;
            }
            else if (s is BreakStatementSyntax || s is ContinueStatementSyntax)
            {
                // 非 goto 的 break/continue（必在内层循环内）；作为块终结以便发射时不追加 fall-through。
                blk.Exit = BlockExit.Break;
            }
            else if (ContainsTopLevelGoto(s))
            {
                // 条件跳转（if 包含 goto）：开新块以承载 fall-through 路径。
                blk.Exit = BlockExit.ConditionalGoto;
                current = new BasicBlock { Index = ++blockIdx };
                blocks.Add(current);
            }
        }

        foreach (var stmt in flat)
        {
            // 标签：开新块并记标签（除非当前块可复用）
            if (stmt is LabeledStatementSyntax labeled)
            {
                NewBlock(labeled.Identifier.ValueText);
                // 标签后的实际语句：labeled.Statement（可能是空语句）
                if (!labeled.Statement.IsKind(SyntaxKind.EmptyStatement))
                {
                    current.Statements.Add(labeled.Statement);
                    // labeled.Statement 仍是终结语句（return/throw/goto/conditional-goto），
                    // 必须同步设置 Exit，否则块被误判为 FallThrough 而追加死代码转移
                    // （XsdDuration 的 InvalidFormat:/Error: 块仅含 return 时即触发）。
                    ApplyExit(current, labeled.Statement);
                }
                continue;
            }

            current.Statements.Add(stmt);
            ApplyExit(current, stmt);
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
            // async 方法的方法体内 return 返回 T（编译器包装为 Task<T>）；末块兜底需用 T
            bool isAsync = visited.Modifiers.Any(SyntaxKind.AsyncKeyword);
            var returnType = UnwrapAsyncReturnType(visited.ReturnType, isAsync);
            var newBody = EmitStateMachine(blocks, prologue, visited.Body, returnType);
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
            // 构造函数无返回类型（视为 void）；操作符/方法取 ReturnType。
            // async 方法的方法体内 return 返回 T（编译器包装为 Task<T>）；末块兜底需用 T
            bool isAsync = node.Modifiers.Any(SyntaxKind.AsyncKeyword);
            TypeSyntax? rawReturnType = node switch
            {
                ConstructorDeclarationSyntax => null,
                OperatorDeclarationSyntax op => op.ReturnType,
                MethodDeclarationSyntax m => m.ReturnType,
                _ => null,
            };
            var returnType = UnwrapAsyncReturnType(rawReturnType, isAsync);
            var newBody = EmitStateMachine(blocks, prologue, body, returnType);
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
        List<BasicBlock> blocks, List<StatementSyntax> hoisted, BlockSyntax originalBody,
        TypeSyntax? returnType)
    {
        // 标签 -> 块 index
        var labelToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
            if (blocks[i].Label != null) labelToIndex[blocks[i].Label!] = i;

        // 预扫描：状态机是否含循环。若含循环，循环内 goto 会用 StateAssignBreak（__exit=true; break;），
        // 需要 __exit 标志 + LoopExitInserter 传播 + GuardedStateAssignContinue + 末尾 break;。
        // 若不含循环，所有 goto 都在循环外，用 StateAssignContinue（无条件 continue），无需 __exit。
        // 关键：若不含循环但仍声明 __exit，编译器因 reset 语句 `if(__exit)__exit=false;` 视 __exit 为
        // 非常量 → GuardedStateAssignContinue 的 if(!__exit) 可能假 → CS0163（case 贯穿）。
        bool needsExitMechanism = StateMachineHasLoops(blocks);

        // 第一遍：改写块语句 + （若需要）应用 LoopExitInserter，收集每个 case 的语句列表。
        var processedCases = new List<List<StatementSyntax>>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var caseStmts = new List<StatementSyntax>(block.Statements);
            // 改写块内 goto / conditional-goto；追加转移
            RewriteBlockStatements(caseStmts, block, labelToIndex, i, blocks, returnType, needsExitMechanism);
            if (needsExitMechanism)
            {
                // 插入循环退出检查（处理循环内 goto 的 break 传播）
                var inserter = new LoopExitInserter();
                for (int s = 0; s < caseStmts.Count; s++)
                {
                    caseStmts[s] = (StatementSyntax)inserter.Visit(caseStmts[s])!;
                }
            }
            processedCases.Add(caseStmts);
        }

        // 第二遍：构建 switch section。若启用 __exit 机制，为以 GuardedStateAssignContinue
        // （if(!__exit){...continue;}）结尾的 case 追加 break;——当 __exit 为 true 时
        // continue 不执行，break; 跳出 switch 由 while 末尾的 __exit 重置后重新分发。
        var sections = new List<SwitchSectionSyntax>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var caseStmts = processedCases[i];
            if (needsExitMechanism)
            {
                bool endsWithGuardedFallthrough =
                    block.Exit == BlockExit.ConditionalGoto
                    || (block.Exit == BlockExit.FallThrough && block.FallThroughTarget != null);
                if (endsWithGuardedFallthrough)
                    caseStmts.Add(SyntaxFactory.BreakStatement());
            }
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

        var whileBodyStmts = new List<StatementSyntax>();
        whileBodyStmts.Add(SyntaxFactory.SwitchStatement(SyntaxFactory.IdentifierName("__state"))
            .WithSections(SyntaxFactory.List(sections)));

        var allStmts = new List<StatementSyntax>();
        allStmts.AddRange(hoisted);
        allStmts.Add(stateDecl);
        if (needsExitMechanism)
        {
            // bool __exit = false;
            var exitDecl = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("__exit")
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))))));
            // if (__exit) __exit = false;  —— 循环内 break 跳出 switch 后重置标志
            var exitReset = SyntaxFactory.IfStatement(
                SyntaxFactory.IdentifierName("__exit"),
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("__exit"),
                        SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))));
            allStmts.Add(exitDecl);
            whileBodyStmts.Add(exitReset);
        }

        // while (true) { switch (__state) { ... } [if (__exit) __exit = false;] }
        var whileStmt = SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(whileBodyStmts));
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
        List<BasicBlock> blocks, TypeSyntax? returnType, bool needsExitMechanism)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            stmts[i] = RewriteNode(stmts[i], labelToIndex);
        }

        // fall-through 转移函数：有循环时用 GuardedStateAssignContinue（if(!__exit)），
        // 无循环时用 StateAssignContinue（无条件 continue）——避免声明 __exit 导致编译器
        // 视其为非常量而报 CS0163。
        StatementSyntax fallthrough(int target)
            => needsExitMechanism ? GuardedStateAssignContinue(target) : StateAssignContinue(target);

        switch (block.Exit)
        {
            case BlockExit.Goto:
                // 末语句是 goto；已被 RewriteNode 改写为 __state=N; continue;
                break;
            case BlockExit.ConditionalGoto:
                // if (c) goto L;  已被改写为 if (c){__state=L;...}（循环内为 break）
                // 追加 fall-through 转移（若循环内已发起 __exit 则跳过，避免覆盖目标 state）
                if (block.FallThroughTarget is int ft)
                    stmts.Add(fallthrough(ft));
                break;
            case BlockExit.FallThrough:
                if (block.FallThroughTarget is int ft2)
                    stmts.Add(fallthrough(ft2));
                else
                    // 末块无 fall-through 目标：方法「落到末尾退出」。
                    // void 方法（returnType 为 null/void）：return; 即隐式返回。
                    // 非 void 方法：本路径不可达（块内必有显式 return/throw，否则原代码就 CS0126），
                    //              但语法上需要返回值，故 emit `return default(T)!;`（永远不可达，仅满足编译器）。
                    // 注意：之前用 StateAssignContinue(selfIndex) 会造成 case 自循环无限重执行
                    // （XmlTextReaderImpl.ParseElement 是 void 且末块无 return → 第二轮重读
                    // _ps.charPos 导致 ArgumentOutOfRangeException）。
                    stmts.Add(BuildUnreachableReturn(returnType));
                break;
            case BlockExit.Return:
            case BlockExit.Break:
                // 块内已含 return/throw/break/continue，无需追加
                break;
        }
    }

    /// <summary>状态机内是否含 for/while/foreach/do 循环——决定是否需要 __exit 标志机制。</summary>
    private static bool StateMachineHasLoops(List<BasicBlock> blocks)
    {
        foreach (var block in blocks)
            foreach (var stmt in block.Statements)
                if (stmt.DescendantNodesAndSelf().Any(n =>
                    n is ForStatementSyntax or WhileStatementSyntax
                    or ForEachStatementSyntax or DoStatementSyntax))
                    return true;
        return false;
    }

    /// <summary>构造末块兜底 return：void 方法返回裸 return;，非 void 方法返回 default(T)!（不可达）。</summary>
    private static StatementSyntax BuildUnreachableReturn(TypeSyntax? returnType)
    {
        bool isVoid = returnType == null
            || (returnType is PredefinedTypeSyntax pt && pt.Keyword.IsKind(SyntaxKind.VoidKeyword));
        if (isVoid)
            return SyntaxFactory.ReturnStatement();
        // default(T)! —— `!` 抑制可空引用类型的 null 警告；值类型上无害。
        var defaultExpr = SyntaxFactory.PostfixUnaryExpression(
            SyntaxKind.SuppressNullableWarningExpression,
            SyntaxFactory.DefaultExpression(returnType!));
        return SyntaxFactory.ReturnStatement(defaultExpr);
    }

    /// <summary>
    /// async 方法的方法体内 return 语句返回的是 T（编译器包装为 Task&lt;T&gt;/ValueTask&lt;T&gt;）。
    /// 因此末块兜底 return 应该用 T 而不是 Task&lt;T&gt;。同理，无泛型参数的 Task/ValueTask 视为 void。
    /// 非 async 方法或非 Task 类型直接返回 returnType。
    /// </summary>
    private static TypeSyntax? UnwrapAsyncReturnType(TypeSyntax? returnType, bool isAsync)
    {
        if (!isAsync || returnType == null) return returnType;
        if (returnType is GenericNameSyntax gn
            && gn.TypeArgumentList.Arguments.Count == 1
            && (gn.Identifier.ValueText == "Task" || gn.Identifier.ValueText == "ValueTask"
                || gn.Identifier.ValueText == "Task`1" || gn.Identifier.ValueText == "ValueTask`1"))
        {
            return gn.TypeArgumentList.Arguments[0];
        }
        if (returnType is IdentifierNameSyntax id
            && (id.Identifier.ValueText == "Task" || id.Identifier.ValueText == "ValueTask"))
        {
            // async Task（无泛型参数）：方法体内不返回值，视为 void
            return null;
        }
        return returnType;
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

    /// <summary>用于循环内的 goto 转换：{ __state=N; __exit=true; break; }。
    /// break 跳出最近的循环/switch；再由 LoopExitInserter 在每层循环体首尾插入的
    /// if(__exit) break; 逐层向外传播，最终跳出 switch(__state)，由 while 末尾的
    /// if(__exit) __exit=false; 重置后重新分发。避免 continue 误续内层循环导致死循环。</summary>
    private static StatementSyntax StateAssignBreak(int target)
        => SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName("__state"),
                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(target)))),
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName("__exit"),
                    SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
            SyntaxFactory.BreakStatement());

    /// <summary>fall-through 转移：若循环内已发起 __exit 转换则跳过，避免覆盖目标 state。</summary>
    private static StatementSyntax GuardedStateAssignContinue(int target)
        => SyntaxFactory.IfStatement(
            SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                SyntaxFactory.IdentifierName("__exit")),
            StateAssignContinue(target));

    /// <summary>把 goto &lt;identifier&gt; 改写。循环内用 { __state=N; __exit=true; break; }（由 LoopExitInserter 传播），
    /// 循环外用 { __state=N; continue; }。递归处理嵌套控制语句内的 goto。</summary>
    private sealed class GotoTransitionRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, int> _labelToIndex;
        private int _loopDepth;
        internal GotoTransitionRewriter(Dictionary<string, int> labelToIndex) { _labelToIndex = labelToIndex; }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        { _loopDepth++; var r = base.VisitForStatement(node); _loopDepth--; return r; }
        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        { _loopDepth++; var r = base.VisitForEachStatement(node); _loopDepth--; return r; }
        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        { _loopDepth++; var r = base.VisitWhileStatement(node); _loopDepth--; return r; }
        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        { _loopDepth++; var r = base.VisitDoStatement(node); _loopDepth--; return r; }

        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            // 此时只可能是 goto <identifier>（goto case/default 已去糖）
            if (node.Expression is IdentifierNameSyntax id
                && _labelToIndex.TryGetValue(id.Identifier.ValueText, out var idx))
            {
                // 循环内的 goto：用 break + __exit 标志，由 LoopExitInserter 逐层传播到状态机 while
                if (_loopDepth > 0)
                    return StateAssignBreak(idx).WithTriviaFrom(node);
                return StateAssignContinue(idx).WithTriviaFrom(node);
            }
            return base.VisitGotoStatement(node);
        }
    }

    /// <summary>在每层循环体末尾和循环后各追加 if(__exit) break;。
    /// 循环体末尾的检查：StateAssignBreak 的 break 仅退出内层 switch（switch(NodeType) 等），
    /// 不退出本层循环；若无此检查，do-while 的 while(cond) 在 __exit=true 后仍被求值，
    /// 调用 Read() 等有副作用的条件，导致 reader 被错误推进（InternalReadContentAsString 的
    /// goto ReturnContent 即触发此 bug）。末尾检查使 break 能进一步跳出本层循环。
    /// 循环后的检查（WrapLoop）：把 __exit 传播到外层循环或 switch(__state)。
    /// 嵌套循环逐层传播：内层末尾退出内层循环→内层 WrapLoop 退出外层循环→外层末尾退出外层循环→外层 WrapLoop 退出 switch。
    /// 注意：不在循环体首部插入检查——那会导致循环在首轮即退出而不执行变量赋值，引发 CS0165。</summary>
    private sealed class LoopExitInserter : CSharpSyntaxRewriter
    {
        internal bool HasLoops { get; private set; }
        private static readonly ExpressionSyntax ExitFlag = SyntaxFactory.IdentifierName("__exit");
        private static StatementSyntax ExitCheck()
            => SyntaxFactory.IfStatement(ExitFlag, SyntaxFactory.BreakStatement());

        private static StatementSyntax WrapLoop(StatementSyntax loop)
            => SyntaxFactory.Block(loop, ExitCheck());

        /// <summary>在循环体末尾追加 if(__exit) break;。若 body 已是 Block 则追加到其语句列表，否则包成 Block。</summary>
        private static StatementSyntax AppendExitCheckToBody(StatementSyntax body)
        {
            if (body is BlockSyntax block)
                return block.WithStatements(block.Statements.Add(ExitCheck()));
            return SyntaxFactory.Block(body, ExitCheck());
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            HasLoops = true;
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            var withExit = visited.WithStatement(AppendExitCheckToBody(visited.Statement));
            return WrapLoop(withExit);
        }
        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            HasLoops = true;
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            var withExit = visited.WithStatement(AppendExitCheckToBody(visited.Statement));
            return WrapLoop(withExit);
        }
        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            HasLoops = true;
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            var withExit = visited.WithStatement(AppendExitCheckToBody(visited.Statement));
            return WrapLoop(withExit);
        }
        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            HasLoops = true;
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            var withExit = visited.WithStatement(AppendExitCheckToBody(visited.Statement));
            return WrapLoop(withExit);
        }
    }
}
