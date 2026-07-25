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
            else if (s is ReturnStatementSyntax || s is ThrowStatementSyntax
                     || s.IsKind(SyntaxKind.YieldBreakStatement))
            {
                blk.Exit = BlockExit.Return;
            }
            else if (s is BreakStatementSyntax || s is ContinueStatementSyntax)
            {
                // 非 goto 的 break/continue（必在内层循环内）；作为块终结以便发射时不追加 fall-through。
                blk.Exit = BlockExit.Break;
            }
            else if (IsTerminatingStatement(s))
            {
                // Block/Switch 等复合语句若自身已终止（例如块末条语句是 return，或 switch 不会贯穿），
                // 同样标记为块终结，避免生成不可达的 fall-through 转移。
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

        void AddStatement(StatementSyntax stmt)
        {
            // 标签：开新块并记标签（除非当前块可复用）
            if (stmt is LabeledStatementSyntax labeled)
            {
                NewBlock(labeled.Identifier.ValueText);
                if (labeled.Statement is LabeledStatementSyntax nestedLabel)
                {
                    AddStatement(nestedLabel);
                    return;
                }
                // 标签后的实际语句：labeled.Statement（可能是空语句）
                if (!labeled.Statement.IsKind(SyntaxKind.EmptyStatement))
                {
                    current.Statements.Add(labeled.Statement);
                    // labeled.Statement 仍是终结语句（return/throw/goto/conditional-goto），
                    // 必须同步设置 Exit，否则标签块会被误判为 FallThrough 并追加死代码转移。
                    ApplyExit(current, labeled.Statement);
                }
                return;
            }

            current.Statements.Add(stmt);
            ApplyExit(current, stmt);
        }

        foreach (var stmt in flat)
        {
            AddStatement(stmt);
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

    /// <summary>递归展开含 label/goto 的裸 BlockSyntax 与 label 后的块；不含的裸块作为单语句保留。</summary>
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
            else if (s is UnsafeStatementSyntax unsafeStatement && ContainsLabelOrGoto(unsafeStatement.Block))
            {
                result.AddRange(Flatten(unsafeStatement.Block.Statements));
            }
            else if (s is LabeledStatementSyntax labeled)
            {
                // 若 label 后面紧跟一个含 label/goto 的块，把块内语句提到当前层级，
                // label 挂在第一条语句上，使内部 label/goto 能被基本块分割识别。
                if (labeled.Statement is BlockSyntax labeledBlock && ContainsLabelOrGoto(labeledBlock))
                {
                    AddFlattenedLabeled(result, labeled, Flatten(labeledBlock.Statements));
                }
                else if (labeled.Statement is UnsafeStatementSyntax labeledUnsafe && ContainsLabelOrGoto(labeledUnsafe.Block))
                {
                    AddFlattenedLabeled(result, labeled, Flatten(labeledUnsafe.Block.Statements));
                }
                else if (labeled.Statement is LabeledStatementSyntax nestedLabel)
                {
                    // 展平嵌套标签链（如 __section: Restart: { ... }），使每层标签都能被识别。
                    var flat = Flatten(SyntaxFactory.SingletonList<StatementSyntax>(nestedLabel));
                    AddFlattenedLabeled(result, labeled, flat);
                }
                else
                {
                    result.Add(s);
                }
            }
            else
            {
                result.Add(s);
            }
        }
        return result;
    }

    private static void AddFlattenedLabeled(List<StatementSyntax> result, LabeledStatementSyntax labeled, List<StatementSyntax> flat)
    {
        if (flat.Count == 0)
        {
            result.Add(labeled);
            return;
        }

        result.Add(labeled.WithStatement(flat[0]));
        for (int i = 1; i < flat.Count; i++)
            result.Add(flat[i]);
    }

    private static bool ContainsLabelOrGoto(SyntaxNode node)
        => node.DescendantNodesAndSelf(ShouldDescendIntoChildren)
            .Any(n => n is LabeledStatementSyntax or GotoStatementSyntax);

    private static bool ShouldDescendIntoChildren(SyntaxNode node)
        => node is not LocalFunctionStatementSyntax
            and not ParenthesizedLambdaExpressionSyntax
            and not SimpleLambdaExpressionSyntax
            and not AnonymousMethodExpressionSyntax;

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
        var hoistedNames = new HashSet<string>();
        var bbList = new List<BasicBlock>();
        foreach (var b in blocks) bbList.Add((BasicBlock)b);

        for (int i = 0; i < bbList.Count; i++)
        {
            var blockStmts = bbList[i].Statements;

            // 新增：扫描嵌套局部声明。C# switch case 内声明的变量作用域覆盖整个 switch，
            // 去糖成状态机后这些声明落在某个基本块的嵌套结构里，却可能被后续基本块引用。
            // 先收集跨块引用的变量名，后续 RemoveConflictingNestedDeclarations 会将其改为赋值。
            var nestedDecls = blockStmts.SelectMany(EnumerateNestedLocalDeclarations).ToList();
            foreach (var decl in nestedDecls)
            {
                if (decl.Modifiers.Any(SyntaxKind.ConstKeyword)) continue;

                bool isUsing = decl.UsingKeyword.IsKind(SyntaxKind.UsingKeyword);
                bool isFixed = decl.Modifiers.Any(SyntaxKind.FixedKeyword)
                               || decl.Modifiers.Any(SyntaxKind.UnsafeKeyword);
                bool isRef = decl.Modifiers.Any(SyntaxKind.RefKeyword)
                             || decl.Modifiers.Any(SyntaxKind.OutKeyword);

                var names = decl.Declaration.Variables.Select(v => v.Identifier.ValueText).ToList();
                bool spans = false;
                for (int j = i + 1; j < bbList.Count; j++)
                    if (bbList[j].Statements.Any(st => ReferencesAny(st, names))) { spans = true; break; }
                if (!spans) continue;

                if (isUsing) throw new GotoEliminatorException("spanning 'using' local not supported");
                if (isFixed) throw new GotoEliminatorException("spanning 'fixed/unsafe' local not supported");
                if (isRef) throw new GotoEliminatorException("spanning 'ref/out' local not supported");

                var type = decl.Declaration.Type;
                var synthesizedType = CleanSynthesizedType(type);
                foreach (var v in decl.Declaration.Variables)
                {
                    if (hoistedNames.Add(v.Identifier.ValueText))
                    {
                        var defaultDecl = SyntaxFactory.LocalDeclarationStatement(
                            SyntaxFactory.VariableDeclaration(synthesizedType.WithTrailingTrivia(SyntaxFactory.Whitespace(" ")),
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.VariableDeclarator(v.Identifier)
                                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                                            SyntaxFactory.DefaultExpression(synthesizedType))))));
                        hoisted.Add(defaultDecl);
                    }
                }
            }

            // 从后向前迭代：提升某声明（尤其无 initializer 时）会令块内语句列表收缩；
            // 若从前向后迭代，会跳过紧随其后的下一条声明，进而漏提升跨块局部。
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
                var synthesizedType = CleanSynthesizedType(type);
                foreach (var v in decl.Declaration.Variables)
                {
                    // 去重：不同 case/分支中同名局部被拍平到同一作用域后会产生重复声明。
                    // 由于原代码这些变量处于互斥作用域，复用同一提升变量是安全的。
                    if (hoistedNames.Add(v.Identifier.ValueText))
                    {
                        var defaultDecl = SyntaxFactory.LocalDeclarationStatement(
                            SyntaxFactory.VariableDeclaration(synthesizedType.WithTrailingTrivia(SyntaxFactory.Whitespace(" ")),
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.VariableDeclarator(v.Identifier)
                                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                                            SyntaxFactory.DefaultExpression(synthesizedType))))));
                        hoisted.Add(defaultDecl);
                    }
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

        // 第二步：扫描所有块（含嵌套控制结构体）中的局部声明。
        // 若某声明的变量名已与已提升变量同名，在拍平到 Java 方法作用域后会产生重复声明；
        // 将其改为赋值（有 initializer）或直接删除（无 initializer），复用已提升变量。
        foreach (var bb in bbList)
        {
            for (int s = 0; s < bb.Statements.Count; s++)
            {
                bb.Statements[s] = RemoveConflictingNestedDeclarations(bb.Statements[s], hoistedNames);
            }
        }

        return hoisted;
    }

    /// <summary>
    /// 递归处理嵌套语句：将名字与已提升变量冲突的局部声明改为赋值或删除。
    /// const/using/fixed/ref 保持原样。
    /// </summary>
    private static StatementSyntax RemoveConflictingNestedDeclarations(StatementSyntax stmt, HashSet<string> hoistedNames)
    {
        switch (stmt)
        {
            case LocalDeclarationStatementSyntax localDecl:
                return RewriteConflictingLocalDeclaration(localDecl, hoistedNames);
            case BlockSyntax block:
                return block.WithStatements(SyntaxFactory.List(block.Statements.Select(s => RemoveConflictingNestedDeclarations(s, hoistedNames))));
            case IfStatementSyntax ifStmt:
                return ifStmt
                    .WithStatement(RemoveConflictingNestedDeclarations(ifStmt.Statement, hoistedNames))
                    .WithElse(ifStmt.Else != null ? SyntaxFactory.ElseClause(RemoveConflictingNestedDeclarations(ifStmt.Else.Statement, hoistedNames)) : null);
            case SwitchStatementSyntax switchStmt:
                return switchStmt.WithSections(SyntaxFactory.List(switchStmt.Sections.Select(sec =>
                    sec.WithStatements(SyntaxFactory.List(sec.Statements.Select(s => RemoveConflictingNestedDeclarations(s, hoistedNames)))))));
            case ForStatementSyntax forStmt:
                return forStmt.WithStatement(RemoveConflictingNestedDeclarations(forStmt.Statement, hoistedNames));
            case ForEachStatementSyntax foreachStmt:
                return foreachStmt.WithStatement(RemoveConflictingNestedDeclarations(foreachStmt.Statement, hoistedNames));
            case WhileStatementSyntax whileStmt:
                return whileStmt.WithStatement(RemoveConflictingNestedDeclarations(whileStmt.Statement, hoistedNames));
            case DoStatementSyntax doStmt:
                return doStmt.WithStatement(RemoveConflictingNestedDeclarations(doStmt.Statement, hoistedNames));
            case TryStatementSyntax tryStmt:
                var newCatches = tryStmt.Catches.Select(c =>
                    c.WithBlock((BlockSyntax)RemoveConflictingNestedDeclarations(c.Block, hoistedNames))).ToList();
                var newFinally = tryStmt.Finally != null
                    ? tryStmt.Finally.WithBlock((BlockSyntax)RemoveConflictingNestedDeclarations(tryStmt.Finally.Block, hoistedNames))
                    : null;
                return tryStmt
                    .WithBlock((BlockSyntax)RemoveConflictingNestedDeclarations(tryStmt.Block, hoistedNames))
                    .WithCatches(SyntaxFactory.List(newCatches))
                    .WithFinally(newFinally);
            case LabeledStatementSyntax labeled:
                return labeled.WithStatement(RemoveConflictingNestedDeclarations(labeled.Statement, hoistedNames));
            case UnsafeStatementSyntax unsafeStmt:
                return unsafeStmt.WithBlock((BlockSyntax)RemoveConflictingNestedDeclarations(unsafeStmt.Block, hoistedNames));
            default:
                return stmt;
        }
    }

    private static StatementSyntax RewriteConflictingLocalDeclaration(LocalDeclarationStatementSyntax decl, HashSet<string> hoistedNames)
    {
        if (decl.Modifiers.Any(SyntaxKind.ConstKeyword))
            return decl;

        bool isUsing = decl.UsingKeyword.IsKind(SyntaxKind.UsingKeyword);
        bool isFixed = decl.Modifiers.Any(SyntaxKind.FixedKeyword)
                       || decl.Modifiers.Any(SyntaxKind.UnsafeKeyword);
        bool isRef = decl.Modifiers.Any(SyntaxKind.RefKeyword)
                     || decl.Modifiers.Any(SyntaxKind.OutKeyword);
        if (isUsing || isFixed || isRef)
            return decl;

        var keptVariables = new List<VariableDeclaratorSyntax>();
        var assignStmts = new List<StatementSyntax>();

        foreach (var v in decl.Declaration.Variables)
        {
            if (hoistedNames.Contains(v.Identifier.ValueText))
            {
                if (v.Initializer != null)
                {
                    var assign = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(v.Identifier), v.Initializer.Value));
                    assignStmts.Add(assign);
                }
            }
            else
            {
                keptVariables.Add(v);
            }
        }

        if (keptVariables.Count == 0)
        {
            if (assignStmts.Count == 1)
                return assignStmts[0];
            if (assignStmts.Count > 1)
                return SyntaxFactory.Block(SyntaxFactory.List(assignStmts));
            return SyntaxFactory.EmptyStatement();
        }

        var newDecl = decl.WithDeclaration(
            decl.Declaration.WithVariables(SyntaxFactory.SeparatedList(keptVariables)));

        if (assignStmts.Count == 0)
            return newDecl;

        return SyntaxFactory.Block(SyntaxFactory.List(assignStmts.Concat(new[] { newDecl })));
    }

    private static bool ReferencesAny(SyntaxNode node, IEnumerable<string> names)
    {
        var set = new HashSet<string>(names);
        return node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(id => set.Contains(id.Identifier.ValueText));
    }

    /// <summary>
    /// 枚举语句内部（非语句自身）的局部声明。不进入局部函数/lambda/匿名方法。
    /// </summary>
    private static IEnumerable<LocalDeclarationStatementSyntax> EnumerateNestedLocalDeclarations(StatementSyntax stmt)
    {
        return stmt.DescendantNodes(ShouldDescendIntoChildren)
            .OfType<LocalDeclarationStatementSyntax>();
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

        var originalGotoCount = CountGotos(visited.Body);
        var body = RewriteNestedClosedBlocks(visited.Body, out var nestedChanged);
        body = DefaultInitializeUnassignedLocals(body);
        if (!ContainsLabelOrGoto(body))
        {
            if (!nestedChanged) return visited;
            bool isAsync = visited.Modifiers.Any(SyntaxKind.AsyncKeyword);
            var returnType = UnwrapAsyncReturnType(visited.ReturnType, isAsync);
            body = AppendUnreachableReturn(body, returnType, IsIteratorBody(body));
            TransformedMethods++;
            GotosEliminated += originalGotoCount;
            return visited.WithBody(body);
        }

        try
        {
            var blocks = SplitToBlocks(body.Statements);
            var hoisted = HoistSpanningLocals(blocks);
            // out 参数在 while 循环前初始化为 default：状态机的 switch(__state) 破坏确定赋值分析，
            // 编译器无法证明所有 case 路径都赋过 out 参数或其结构字段。
            var prologue = BuildOutParameterInitializers(visited);
            prologue.AddRange(hoisted);
            // async 方法的方法体内 return 返回 T（编译器包装为 Task<T>）；末块兜底需用 T
            bool isAsync = visited.Modifiers.Any(SyntaxKind.AsyncKeyword);
            var returnType = UnwrapAsyncReturnType(visited.ReturnType, isAsync);
            var newBody = EmitStateMachine(blocks, prologue, body, returnType, IsIteratorBody(body));
            TransformedMethods++;
            GotosEliminated += originalGotoCount;
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
    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        => RewriteBaseMethod(node, n => n.Body, (n, b) => n.WithBody(b));
    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var visited = node;
        if (visited.Body == null || !ContainsLabelOrGoto(visited.Body)) return visited;
        var originalGotoCount = CountGotos(visited.Body);
        var body = RewriteNestedClosedBlocks(visited.Body, out var nestedChanged);
        body = DefaultInitializeUnassignedLocals(body);
        if (!ContainsLabelOrGoto(body))
        {
            if (!nestedChanged) return visited;
            bool isAsync = visited.Modifiers.Any(SyntaxKind.AsyncKeyword);
            var returnType = UnwrapAsyncReturnType(visited.ReturnType, isAsync);
            body = AppendUnreachableReturn(body, returnType, IsIteratorBody(body));
            TransformedMethods++;
            GotosEliminated += originalGotoCount;
            return visited.WithBody(body);
        }

        try
        {
            var blocks = SplitToBlocks(body.Statements);
            var hoisted = HoistSpanningLocals(blocks);
            var prologue = BuildOutParameterInitializers(visited.ParameterList);
            prologue.AddRange(hoisted);
            bool isAsync = visited.Modifiers.Any(SyntaxKind.AsyncKeyword);
            var returnType = UnwrapAsyncReturnType(visited.ReturnType, isAsync);
            var newBody = EmitStateMachine(blocks, prologue, body, returnType, IsIteratorBody(body));
            TransformedMethods++;
            GotosEliminated += originalGotoCount;
            return visited.WithBody(newBody);
        }
        catch (GotoEliminatorException ex)
        {
            SkippedMethods++;
            Diagnostics.Add(new GotoEliminatorDiagnostic(
                GotoEliminatorSeverity.Warning, ex.Message,
                visited.Identifier.ValueText));
            return visited;
        }
    }

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
        var originalGotoCount = CountGotos(body);
        var rewrittenBody = RewriteNestedClosedBlocks(body, out var nestedChanged);
        rewrittenBody = DefaultInitializeUnassignedLocals(rewrittenBody);
        if (!ContainsLabelOrGoto(rewrittenBody))
        {
            if (!nestedChanged) return visited;
            bool isAsync = node.Modifiers.Any(SyntaxKind.AsyncKeyword);
            TypeSyntax? rawReturnType = node switch
            {
                ConstructorDeclarationSyntax => null,
                OperatorDeclarationSyntax op => op.ReturnType,
                ConversionOperatorDeclarationSyntax conv => conv.Type,
                MethodDeclarationSyntax m => m.ReturnType,
                _ => null,
            };
            var returnType = UnwrapAsyncReturnType(rawReturnType, isAsync);
            rewrittenBody = AppendUnreachableReturn(rewrittenBody, returnType, IsIteratorBody(rewrittenBody));
            TransformedMethods++;
            GotosEliminated += originalGotoCount;
            return withBody(visited, rewrittenBody);
        }

        try
        {
            var blocks = SplitToBlocks(rewrittenBody.Statements);
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
                ConversionOperatorDeclarationSyntax conv => conv.Type,
                MethodDeclarationSyntax m => m.ReturnType,
                _ => null,
            };
            var returnType = UnwrapAsyncReturnType(rawReturnType, isAsync);
            var newBody = EmitStateMachine(blocks, prologue, rewrittenBody, returnType, IsIteratorBody(rewrittenBody));
            TransformedMethods++;
            GotosEliminated += originalGotoCount;
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
        => BuildOutParameterInitializers(method.ParameterList);

    private static List<StatementSyntax> BuildOutParameterInitializers(ParameterListSyntax? parameterList)
    {
        var result = new List<StatementSyntax>();
        if (parameterList == null) return result;
        foreach (var p in parameterList.Parameters)
        {
            if (!p.Modifiers.Any(SyntaxKind.OutKeyword)) continue;
            if (p.Type == null) continue;
            // default(T)! —— `!` 抑制可空引用类型的 null 警告；值类型上无害。
            var defaultExpr = SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                SyntaxFactory.DefaultExpression(CleanSynthesizedType(p.Type)));
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

    private static bool IsIteratorBody(BlockSyntax body)
        => body.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .Any();

    private static BlockSyntax RewriteNestedClosedBlocks(BlockSyntax body, out bool changed)
    {
        var rewriter = new NestedClosedBlockRewriter(body);
        var rewritten = (BlockSyntax)rewriter.Visit(body)!;
        changed = rewriter.Changed;
        return rewritten;
    }

    private sealed class NestedClosedBlockRewriter : CSharpSyntaxRewriter
    {
        private readonly BlockSyntax _root;
        private readonly HashSet<string> _usedNames;
        private int _nameIndex;

        internal bool Changed { get; private set; }

        internal NestedClosedBlockRewriter(BlockSyntax root)
        {
            _root = root;
            _usedNames = root.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.IdentifierToken))
                .Select(t => t.ValueText)
                .ToHashSet(StringComparer.Ordinal);
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

        public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
        {
            var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;
            if (!ContainsLabelOrGoto(visited)) return visited;
            // 将含 label/goto 的 switch 降级为 goto 分发的语句块，使原本嵌套在 switch
            // 节中的标签进入外层作用域，可被基本块分割识别。自闭合性不是必要条件：
            // switch 节中的 break 会被重写到退出标签，跨越 switch 边界的 goto 仍保留，
            // 后续由外层状态机统一处理。
            return LowerSwitchToBlock(visited);
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax)base.VisitBlock(node)!;
            if (node.Span == _root.Span || !ContainsLabelOrGoto(visited) || !HasClosedLocalLabels(node, visited))
                return visited;

            try
            {
                var blocks = SplitToBlocks(visited.Statements);
                var hoisted = HoistSpanningLocals(blocks);
                var stateName = NextName("__cs2jBlockState");
                var exitName = NextName("__cs2jBlockExit");
                var breakName = NextName("__cs2jBlockBreak");
                var continueName = NextName("__cs2jBlockContinue");
                var lowered = EmitNestedBlockStateMachine(
                    blocks,
                    hoisted,
                    visited,
                    stateName,
                    exitName,
                    breakName,
                    continueName,
                    CanBreakFrom(node) && ContainsEscapingBreak(visited),
                    CanContinueFrom(node) && ContainsEscapingContinue(visited));
                Changed = true;
                return lowered.WithTriviaFrom(visited);
            }
            catch (GotoEliminatorException)
            {
                return visited;
            }
        }

        /// <summary>把含 label/goto 的 switch 降为由 goto 分发的语句块，
        /// 使原本嵌套在 switch 节中的 label 进入外层基本块，可被状态机处理。</summary>
        private BlockSyntax LowerSwitchToBlock(SwitchStatementSyntax switchStatement)
        {
            var exitLabel = NextName("__cs2jSwitchExit");
            var sectionLabels = new List<string>();
            var dispatcherSections = new List<SwitchSectionSyntax>();
            var sectionBodies = new List<StatementSyntax>();

            foreach (var section in switchStatement.Sections)
            {
                var secLabel = NextName("__cs2jSwitchSection");
                sectionLabels.Add(secLabel);
                dispatcherSections.Add(SyntaxFactory.SwitchSection(
                    section.Labels,
                    SyntaxFactory.SingletonList<StatementSyntax>(
                        SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement, SyntaxFactory.IdentifierName(secLabel)))));

                var breakRewriter = new BreakToExitRewriter(exitLabel);
                var rewritten = section.Statements.Select(s => (StatementSyntax)breakRewriter.Visit(s)!).ToList();
                var body = rewritten.Count == 1 ? rewritten[0] : SyntaxFactory.Block(rewritten);
                sectionBodies.Add(SyntaxFactory.LabeledStatement(SyntaxFactory.Identifier(secLabel), body));
            }

            // 原 switch 没有 default 时，不匹配任何 case 的值会贯穿到 switch 之后。
            // 在降级的 dispatcher 中补一个 default: goto __exit，否则 dispatcher 可能落入 section body，
            // 导致嵌套状态机出现 case 贯穿（CS0163）或语义错误。
            bool hasDefault = switchStatement.Sections.Any(s => s.Labels.Any(l => l.IsKind(SyntaxKind.DefaultSwitchLabel)));
            if (!hasDefault)
            {
                dispatcherSections.Add(SyntaxFactory.SwitchSection(
                    SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.DefaultSwitchLabel()),
                    SyntaxFactory.SingletonList<StatementSyntax>(
                        SyntaxFactory.GotoStatement(SyntaxKind.GotoStatement, SyntaxFactory.IdentifierName(exitLabel)))));
            }

            var stmts = new List<StatementSyntax>
            {
                SyntaxFactory.SwitchStatement(switchStatement.Expression, SyntaxFactory.List(dispatcherSections)),
            };

            // dispatcher 现在覆盖所有值（显式 case + default），每个分支都以 goto 结束，
            // 不会再落入后面的 section body，因此不需要再追加无条件 goto __exit。
            stmts.AddRange(sectionBodies);
            stmts.Add(SyntaxFactory.LabeledStatement(SyntaxFactory.Identifier(exitLabel), SyntaxFactory.EmptyStatement()));

            Changed = true;
            return SyntaxFactory.Block(stmts).WithTriviaFrom(switchStatement);
        }

        private sealed class BreakToExitRewriter : CSharpSyntaxRewriter
        {
            private readonly string _exitLabel;
            private int _depth;
            internal BreakToExitRewriter(string exitLabel) => _exitLabel = exitLabel;

            public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;
            public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;
            public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;
            public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

            public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
            {
                _depth++;
                var result = base.VisitForStatement(node);
                _depth--;
                return result;
            }

            public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
            {
                _depth++;
                var result = base.VisitForEachStatement(node);
                _depth--;
                return result;
            }

            public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
            {
                _depth++;
                var result = base.VisitWhileStatement(node);
                _depth--;
                return result;
            }

            public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
            {
                _depth++;
                var result = base.VisitDoStatement(node);
                _depth--;
                return result;
            }

            public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
            {
                _depth++;
                var result = base.VisitSwitchStatement(node);
                _depth--;
                return result;
            }

            public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node)
            {
                if (_depth == 0)
                {
                    return SyntaxFactory.GotoStatement(
                        SyntaxKind.GotoStatement,
                        SyntaxFactory.IdentifierName(_exitLabel)).WithTriviaFrom(node);
                }
                return base.VisitBreakStatement(node);
            }
        }

        private string NextName(string prefix)
        {
            string name;
            do
            {
                name = prefix + _nameIndex++;
            }
            while (!_usedNames.Add(name));
            return name;
        }

        private bool HasClosedLocalLabels(BlockSyntax originalBlock, BlockSyntax visitedBlock)
        {
            var labels = visitedBlock.DescendantNodesAndSelf(ShouldDescendIntoChildren)
                .OfType<LabeledStatementSyntax>()
                .Select(l => l.Identifier.ValueText)
                .ToHashSet(StringComparer.Ordinal);
            if (labels.Count == 0) return false;

            var internalGotos = visitedBlock.DescendantNodesAndSelf(ShouldDescendIntoChildren)
                .OfType<GotoStatementSyntax>()
                .Where(g => g.Expression is IdentifierNameSyntax)
                .ToList();
            if (internalGotos.Count == 0) return false;
            if (!internalGotos.Any(g => labels.Contains(((IdentifierNameSyntax)g.Expression!).Identifier.ValueText)))
                return false;

            var blockSpan = originalBlock.Span;
            var outsideTargets = _root.DescendantNodesAndSelf(ShouldDescendIntoChildren)
                .OfType<GotoStatementSyntax>()
                .Where(g => !blockSpan.Contains(g.Span))
                .Select(g => g.Expression as IdentifierNameSyntax)
                .Where(id => id != null)
                .Select(id => id!.Identifier.ValueText);
            return !outsideTargets.Any(labels.Contains);
        }

        private static bool CanBreakFrom(SyntaxNode node)
            => node.Ancestors()
                .TakeWhile(IsSameExecutableBody)
                .Any(a => a is ForStatementSyntax or ForEachStatementSyntax
                    or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax);

        private static bool CanContinueFrom(SyntaxNode node)
            => node.Ancestors()
                .TakeWhile(IsSameExecutableBody)
                .Any(a => a is ForStatementSyntax or ForEachStatementSyntax
                    or WhileStatementSyntax or DoStatementSyntax);

        private static bool ContainsEscapingBreak(BlockSyntax block)
            => block.DescendantNodesAndSelf(ShouldDescendIntoChildren)
                .OfType<BreakStatementSyntax>()
                .Any(b => !b.Ancestors()
                    .TakeWhile(a => a != block)
                    .Any(a => a is ForStatementSyntax or ForEachStatementSyntax
                        or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax));

        private static bool ContainsEscapingContinue(BlockSyntax block)
            => block.DescendantNodesAndSelf(ShouldDescendIntoChildren)
                .OfType<ContinueStatementSyntax>()
                .Any(c => !c.Ancestors()
                    .TakeWhile(a => a != block)
                    .Any(a => a is ForStatementSyntax or ForEachStatementSyntax
                        or WhileStatementSyntax or DoStatementSyntax));

        private static bool IsSameExecutableBody(SyntaxNode node)
            => node is not BaseMethodDeclarationSyntax
                and not LocalFunctionStatementSyntax
                and not ParenthesizedLambdaExpressionSyntax
                and not SimpleLambdaExpressionSyntax
                and not AnonymousMethodExpressionSyntax;
    }

    /// <summary>发射 while(true){switch(__state){case i: ...}} 并替换原方法体。</summary>
    internal static BlockSyntax EmitStateMachine(
        List<BasicBlock> blocks, List<StatementSyntax> hoisted, BlockSyntax originalBody,
        TypeSyntax? returnType, bool isIterator = false)
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
            RewriteBlockStatements(caseStmts, block, labelToIndex, i, blocks, returnType, needsExitMechanism, isIterator);
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

    private static BlockSyntax EmitNestedBlockStateMachine(
        List<BasicBlock> blocks,
        List<StatementSyntax> hoisted,
        BlockSyntax originalBlock,
        string stateName,
        string exitName,
        string breakName,
        string continueName,
        bool canBreak,
        bool canContinue)
    {
        var labelToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
            if (blocks[i].Label != null) labelToIndex[blocks[i].Label!] = i;

        bool needsExitMechanism = StateMachineHasLoops(blocks);
        var processedCases = new List<List<StatementSyntax>>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var caseStmts = new List<StatementSyntax>(block.Statements);
            RewriteNestedBlockStatements(
                caseStmts,
                block,
                labelToIndex,
                blocks,
                stateName,
                exitName,
                breakName,
                continueName,
                canBreak,
                canContinue,
                needsExitMechanism);
            if (needsExitMechanism)
            {
                var inserter = new NamedLoopExitInserter(exitName);
                for (int s = 0; s < caseStmts.Count; s++)
                {
                    caseStmts[s] = (StatementSyntax)inserter.Visit(caseStmts[s])!;
                }
            }

            processedCases.Add(caseStmts);
        }

        var sections = new List<SwitchSectionSyntax>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var caseStmts = processedCases[i];
            if (needsExitMechanism)
            {
                bool endsWithGuardedFallthrough =
                    block.Exit == BlockExit.ConditionalGoto
                    || block.Exit == BlockExit.FallThrough;
                if (endsWithGuardedFallthrough)
                    caseStmts.Add(SyntaxFactory.BreakStatement());
            }

            sections.Add(SyntaxFactory.SwitchSection(
                SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.CaseSwitchLabel(Number(block.Index))),
                SyntaxFactory.List(caseStmts)));
        }

        var stateDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(stateName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(Number(0))))));

        var whileBodyStmts = new List<StatementSyntax>
        {
            SyntaxFactory.SwitchStatement(SyntaxFactory.IdentifierName(stateName))
                .WithSections(SyntaxFactory.List(sections))
        };

        var allStmts = new List<StatementSyntax>();
        allStmts.AddRange(hoisted);
        allStmts.Add(stateDecl);

        if (needsExitMechanism)
        {
            allStmts.Add(BoolDeclaration(exitName));
            whileBodyStmts.Add(AssignBoolIfTrue(exitName, false));
        }

        if (canBreak)
            allStmts.Add(BoolDeclaration(breakName));
        if (canContinue)
            allStmts.Add(BoolDeclaration(continueName));

        allStmts.Add(SyntaxFactory.WhileStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.GreaterThanOrEqualExpression,
                SyntaxFactory.IdentifierName(stateName),
                Number(0)),
            SyntaxFactory.Block(whileBodyStmts)));

        if (canBreak)
        {
            allStmts.Add(SyntaxFactory.IfStatement(
                SyntaxFactory.IdentifierName(breakName),
                SyntaxFactory.BreakStatement()));
        }

        if (canContinue)
        {
            allStmts.Add(SyntaxFactory.IfStatement(
                SyntaxFactory.IdentifierName(continueName),
                SyntaxFactory.ContinueStatement()));
        }

        if (allStmts.Count > 0 && originalBlock.Statements.Count > 0)
            allStmts[0] = allStmts[0].WithLeadingTrivia(originalBlock.Statements[0].GetLeadingTrivia());

        return SyntaxFactory.Block(allStmts);
    }

    /// <summary>改写块内语句：goto label -> __state=N; continue; ；追加块转移。</summary>
    private static void RewriteBlockStatements(
        List<StatementSyntax> stmts, BasicBlock block,
        Dictionary<string, int> labelToIndex, int selfIndex,
        List<BasicBlock> blocks, TypeSyntax? returnType, bool needsExitMechanism, bool isIterator)
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
                else
                    stmts.Add(BuildUnreachableReturn(returnType, isIterator));
                break;
            case BlockExit.FallThrough:
                if (block.FallThroughTarget is int ft2)
                    stmts.Add(fallthrough(ft2));
                else
                    // 末块无 fall-through 目标：方法「落到末尾退出」。
                    // void 方法（returnType 为 null/void）：return; 即隐式返回。
                    // 非 void 方法：本路径不可达（块内必有显式 return/throw，否则原代码就 CS0126），
                    //              但语法上需要返回值，故 emit `return default(T)!;`（永远不可达，仅满足编译器）。
                    // 注意：不能回到当前 state；末块无 fall-through 目标时应退出方法，
                    // 否则会自循环并重执行有副作用的语句。
                    stmts.Add(BuildUnreachableReturn(returnType, isIterator));
                break;
            case BlockExit.Return:
            case BlockExit.Break:
                // 块内已含 return/throw/break/continue，无需追加
                break;
        }
    }

    private static void RewriteNestedBlockStatements(
        List<StatementSyntax> stmts,
        BasicBlock block,
        Dictionary<string, int> labelToIndex,
        List<BasicBlock> blocks,
        string stateName,
        string exitName,
        string breakName,
        string continueName,
        bool canBreak,
        bool canContinue,
        bool needsExitMechanism)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            stmts[i] = RewriteNestedNode(
                stmts[i],
                labelToIndex,
                stateName,
                exitName,
                breakName,
                continueName,
                canBreak,
                canContinue);
        }

        StatementSyntax fallthrough(int target)
            => needsExitMechanism
                ? GuardedAssignStateContinue(stateName, exitName, target)
                : AssignStateContinue(stateName, target);

        switch (block.Exit)
        {
            case BlockExit.Goto:
                break;
            case BlockExit.ConditionalGoto:
                if (block.FallThroughTarget is int ft)
                    stmts.Add(fallthrough(ft));
                else
                    stmts.Add(needsExitMechanism
                        ? GuardedExitNestedBlock(stateName, exitName)
                        : ExitNestedBlock(stateName));
                break;
            case BlockExit.FallThrough:
                if (block.FallThroughTarget is int ft2)
                    stmts.Add(fallthrough(ft2));
                else
                    stmts.Add(needsExitMechanism
                        ? GuardedExitNestedBlock(stateName, exitName)
                        : ExitNestedBlock(stateName));
                break;
            case BlockExit.Return:
            case BlockExit.Break:
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
    private static StatementSyntax BuildUnreachableReturn(TypeSyntax? returnType, bool isIterator)
    {
        if (isIterator)
            return SyntaxFactory.YieldStatement(SyntaxKind.YieldBreakStatement);
        bool isVoid = returnType == null
            || (returnType is PredefinedTypeSyntax pt && pt.Keyword.IsKind(SyntaxKind.VoidKeyword));
        if (isVoid)
            return SyntaxFactory.ReturnStatement();
        // default(T)! —— `!` 抑制可空引用类型的 null 警告；值类型上无害。
        var defaultExpr = SyntaxFactory.PostfixUnaryExpression(
            SyntaxKind.SuppressNullableWarningExpression,
            SyntaxFactory.DefaultExpression(CleanSynthesizedType(returnType!)));
        return SyntaxFactory.ReturnStatement(defaultExpr);
    }

    private static BlockSyntax AppendUnreachableReturn(
        BlockSyntax body,
        TypeSyntax? returnType,
        bool isIterator)
    {
        bool isVoid = returnType == null
            || (returnType is PredefinedTypeSyntax pt && pt.Keyword.IsKind(SyntaxKind.VoidKeyword));
        if (isVoid)
            return body;
        return body.WithStatements(body.Statements.Add(BuildUnreachableReturn(returnType, isIterator)));
    }

    private static BlockSyntax DefaultInitializeUnassignedLocals(BlockSyntax body)
        => (BlockSyntax)new UnassignedLocalInitializer().Visit(body)!;

    private sealed class UnassignedLocalInitializer : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.ConstKeyword)
                || node.Modifiers.Any(SyntaxKind.RefKeyword)
                || node.Modifiers.Any(SyntaxKind.OutKeyword)
                || node.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
            {
                return node;
            }

            if (node.Declaration.Type is IdentifierNameSyntax id
                && id.Identifier.ValueText == "var")
            {
                return node;
            }

            var variables = node.Declaration.Variables;
            if (variables.All(v => v.Initializer != null))
                return node;

            var initialized = variables.Select(v => v.Initializer == null
                ? v.WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.DefaultExpression(CleanSynthesizedType(node.Declaration.Type))))
                : v);

            return node.WithDeclaration(node.Declaration.WithVariables(
                SyntaxFactory.SeparatedList(initialized, variables.GetSeparators())));
        }
    }

    private static LiteralExpressionSyntax Number(int value)
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(value));

    /// <summary>
    /// 识别由 EmitNestedBlockStateMachine/EmitStateMachine 生成的状态机 while 循环
    ///（条件为 stateName >= 0）。这类 while 内的 goto 若用普通 break 只会退出 switch，
    /// 无法退出状态机 while，需要特殊处理。
    /// </summary>
    private static string? TryGetGeneratedStateMachineStateName(WhileStatementSyntax whileStmt)
    {
        if (whileStmt.Condition is BinaryExpressionSyntax bin
            && bin.OperatorToken.IsKind(SyntaxKind.GreaterThanEqualsToken)
            && bin.Left is IdentifierNameSyntax id
            && bin.Right is LiteralExpressionSyntax lit
            && lit.Token.Value is int v && v == 0
            && id.Identifier.ValueText.StartsWith("__cs2jBlockState", StringComparison.Ordinal))
        {
            return id.Identifier.ValueText;
        }
        return null;
    }

    private static TypeSyntax CleanSynthesizedType(TypeSyntax type)
        => type.WithoutTrivia();

    private static StatementSyntax BoolDeclaration(string name)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(name)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))))));

    private static StatementSyntax AssignBoolIfTrue(string name, bool value)
        => SyntaxFactory.IfStatement(
            SyntaxFactory.IdentifierName(name),
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(name),
                    SyntaxFactory.LiteralExpression(value
                        ? SyntaxKind.TrueLiteralExpression
                        : SyntaxKind.FalseLiteralExpression))));

    private static StatementSyntax AssignStateBreak(
        string stateName,
        string exitName,
        int target)
        => SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(stateName),
                    Number(target))),
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(exitName),
                    SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
            SyntaxFactory.BreakStatement());

    private static StatementSyntax AssignStateContinue(string stateName, int target)
        => SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(stateName),
                    Number(target))),
            SyntaxFactory.ContinueStatement());

    private static StatementSyntax GuardedAssignStateContinue(
        string stateName,
        string exitName,
        int target)
        => SyntaxFactory.IfStatement(
            SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.IdentifierName(exitName)),
            AssignStateContinue(stateName, target));

    private static StatementSyntax ExitNestedBlock(string stateName)
        => SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(stateName),
                    Number(-1))),
            SyntaxFactory.ContinueStatement());

    private static StatementSyntax GuardedExitNestedBlock(string stateName, string exitName)
        => SyntaxFactory.IfStatement(
            SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.IdentifierName(exitName)),
            ExitNestedBlock(stateName));

    private static StatementSyntax SignalControlAndExit(
        string stateName,
        string signalName)
        => SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(signalName),
                    SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(stateName),
                    Number(-1))),
            SyntaxFactory.ContinueStatement());

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

    private static StatementSyntax RewriteNestedNode(
        SyntaxNode node,
        Dictionary<string, int> labelToIndex,
        string stateName,
        string exitName,
        string breakName,
        string continueName,
        bool canBreak,
        bool canContinue)
    {
        var rewriter = new NestedBlockTransitionRewriter(
            labelToIndex,
            stateName,
            exitName,
            breakName,
            continueName,
            canBreak,
            canContinue);
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
        private readonly Stack<string> _generatedStateMachineStates = new();
        internal GotoTransitionRewriter(Dictionary<string, int> labelToIndex) { _labelToIndex = labelToIndex; }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        { _loopDepth++; var r = base.VisitForStatement(node); _loopDepth--; return r; }
        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        { _loopDepth++; var r = base.VisitForEachStatement(node); _loopDepth--; return r; }
        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            _loopDepth++;
            var generatedState = TryGetGeneratedStateMachineStateName(node);
            if (generatedState != null) _generatedStateMachineStates.Push(generatedState);
            var r = base.VisitWhileStatement(node);
            if (generatedState != null) _generatedStateMachineStates.Pop();
            _loopDepth--;
            return r;
        }
        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        { _loopDepth++; var r = base.VisitDoStatement(node); _loopDepth--; return r; }

        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            // 此时只可能是 goto <identifier>（goto case/default 已去糖）
            if (node.Expression is IdentifierNameSyntax id
                && _labelToIndex.TryGetValue(id.Identifier.ValueText, out var idx))
            {
                // 若 goto 位于已生成的嵌套状态机 while 内部，普通 break 只能退出内层 switch，
                // 无法退出内层状态机 while，会导致死循环。此时把内层状态设为 -1 并 continue，
                // 让内层状态机退出，同时在外层状态机中继续目标状态。
                if (_generatedStateMachineStates.Count > 0)
                    return BreakThroughGeneratedStateMachine(idx, _generatedStateMachineStates.Peek()).WithTriviaFrom(node);

                // 循环内的 goto：用 break + __exit 标志，由 LoopExitInserter 逐层传播到状态机 while
                if (_loopDepth > 0)
                    return StateAssignBreak(idx).WithTriviaFrom(node);
                return StateAssignContinue(idx).WithTriviaFrom(node);
            }
            return base.VisitGotoStatement(node);
        }

        private static StatementSyntax BreakThroughGeneratedStateMachine(int target, string innerStateName)
            => SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("__state"),
                        Number(target))),
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName("__exit"),
                        SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(innerStateName),
                        Number(-1))),
                SyntaxFactory.ContinueStatement());
    }

    private sealed class NestedBlockTransitionRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, int> _labelToIndex;
        private readonly string _stateName;
        private readonly string _exitName;
        private readonly string _breakName;
        private readonly string _continueName;
        private readonly bool _canBreak;
        private readonly bool _canContinue;
        private int _loopDepth;
        private int _breakableDepth;
        private int _continuableDepth;
        private readonly Stack<string> _generatedStateMachineStates = new();

        internal NestedBlockTransitionRewriter(
            Dictionary<string, int> labelToIndex,
            string stateName,
            string exitName,
            string breakName,
            string continueName,
            bool canBreak,
            bool canContinue)
        {
            _labelToIndex = labelToIndex;
            _stateName = stateName;
            _exitName = exitName;
            _breakName = breakName;
            _continueName = continueName;
            _canBreak = canBreak;
            _canContinue = canContinue;
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            _loopDepth++;
            _breakableDepth++;
            _continuableDepth++;
            var r = base.VisitForStatement(node);
            _continuableDepth--;
            _breakableDepth--;
            _loopDepth--;
            return r;
        }
        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            _loopDepth++;
            _breakableDepth++;
            _continuableDepth++;
            var r = base.VisitForEachStatement(node);
            _continuableDepth--;
            _breakableDepth--;
            _loopDepth--;
            return r;
        }
        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            _loopDepth++;
            _breakableDepth++;
            _continuableDepth++;
            var generatedState = TryGetGeneratedStateMachineStateName(node);
            if (generatedState != null) _generatedStateMachineStates.Push(generatedState);
            var r = base.VisitWhileStatement(node);
            if (generatedState != null) _generatedStateMachineStates.Pop();
            _continuableDepth--;
            _breakableDepth--;
            _loopDepth--;
            return r;
        }
        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            _loopDepth++;
            _breakableDepth++;
            _continuableDepth++;
            var r = base.VisitDoStatement(node);
            _continuableDepth--;
            _breakableDepth--;
            _loopDepth--;
            return r;
        }

        public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
        {
            _breakableDepth++;
            var result = base.VisitSwitchStatement(node);
            _breakableDepth--;
            return result;
        }

        public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node)
        {
            if (_breakableDepth == 0 && _canBreak)
                return SignalControlAndExit(_stateName, _breakName).WithTriviaFrom(node);
            return node;
        }

        public override SyntaxNode? VisitContinueStatement(ContinueStatementSyntax node)
        {
            if (_continuableDepth == 0 && _canContinue)
                return SignalControlAndExit(_stateName, _continueName).WithTriviaFrom(node);
            return node;
        }

        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            if (node.Expression is IdentifierNameSyntax id
                && _labelToIndex.TryGetValue(id.Identifier.ValueText, out var idx))
            {
                // 若 goto 位于已生成的嵌套状态机 while 内部，普通 break 只能退出内层 switch，
                // 无法退出内层状态机 while，会导致死循环。此时把内层状态设为 -1 并 continue，
                // 让内层状态机退出，同时在本层状态机中继续目标状态。
                if (_generatedStateMachineStates.Count > 0)
                    return BreakThroughGeneratedStateMachine(idx, _generatedStateMachineStates.Peek()).WithTriviaFrom(node);

                if (_loopDepth > 0)
                    return AssignStateBreak(_stateName, _exitName, idx).WithTriviaFrom(node);
                return AssignStateContinue(_stateName, idx).WithTriviaFrom(node);
            }

            return base.VisitGotoStatement(node);
        }

        private StatementSyntax BreakThroughGeneratedStateMachine(int target, string innerStateName)
            => SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(_stateName),
                        Number(target))),
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(_exitName),
                        SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(innerStateName),
                        Number(-1))),
                SyntaxFactory.ContinueStatement());
    }

    /// <summary>判断 switch 是否存在至少一个可以贯穿到 switch 之后的 section。</summary>
    private static bool CanSwitchFallThrough(SwitchStatementSyntax switchStmt)
    {
        foreach (var section in switchStmt.Sections)
            if (CanSwitchSectionFallThrough(section))
                return true;
        return false;
    }

    /// <summary>判断单个 switch section 是否可以执行到 switch 之后。
    /// 注意：section 末尾的 break 会退出 switch 并到达 switch 之后，因此视为可贯穿；
    /// continue/return/throw/goto 则跳转到 switch 之外的其他位置，视为不可贯穿。</summary>
    private static bool CanSwitchSectionFallThrough(SwitchSectionSyntax section)
    {
        if (section.Statements.Count == 0)
            return true;
        return CanStatementReachSwitchEnd(section.Statements.Last());
    }

    /// <summary>判断一条语句执行后是否能到达所在 switch 的末尾（即 switch 之后）。</summary>
    private static bool CanStatementReachSwitchEnd(StatementSyntax statement)
    {
        switch (statement)
        {
            case BreakStatementSyntax:
                // break 退出当前 switch，控制流到达 switch 之后。
                return true;
            case ContinueStatementSyntax:
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case GotoStatementSyntax:
                // 这些语句跳转到 switch 之外的其他位置，不会到达 switch 之后。
                return false;
            case BlockSyntax block:
                return block.Statements.Count > 0 && CanStatementReachSwitchEnd(block.Statements.Last());
            case SwitchStatementSyntax sw:
                return CanSwitchFallThrough(sw);
            case IfStatementSyntax iff:
                if (iff.Else == null)
                    return CanStatementReachSwitchEnd(iff.Statement);
                return CanStatementReachSwitchEnd(iff.Statement) || CanStatementReachSwitchEnd(iff.Else.Statement);
            default:
                return true;
        }
    }

    /// <summary>判断一条语句是否是终止性语句（控制流不会继续到下一条语句）。</summary>
    private static bool IsTerminatingStatement(StatementSyntax statement)
    {
        switch (statement)
        {
            case BreakStatementSyntax:
            case ContinueStatementSyntax:
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case GotoStatementSyntax:
                return true;
            case BlockSyntax block:
                return block.Statements.Count > 0 && IsTerminatingStatement(block.Statements.Last());
            case SwitchStatementSyntax sw:
                return !CanSwitchFallThrough(sw);
            case TryStatementSyntax tryStmt:
                return !CanTryStatementFallThrough(tryStmt);
            default:
                return false;
        }
    }

    /// <summary>
    /// 判断 try 语句执行后是否可能继续执行其后的兄弟语句。
    /// 含 finally 时由 finally 块决定；否则只要 try 块或任一 catch 块能贯穿，整个语句就能贯穿。
    /// </summary>
    private static bool CanTryStatementFallThrough(TryStatementSyntax tryStmt)
    {
        if (tryStmt.Finally != null)
            return CanStatementFallThrough(tryStmt.Finally.Block);

        if (CanStatementFallThrough(tryStmt.Block))
            return true;

        foreach (var catchClause in tryStmt.Catches)
        {
            if (CanStatementFallThrough(catchClause.Block))
                return true;
        }

        return false;
    }

    /// <summary>判断一条语句执行后是否可能继续执行其后的兄弟语句。</summary>
    private static bool CanStatementFallThrough(StatementSyntax statement)
    {
        switch (statement)
        {
            case BlockSyntax block:
                // Conservative: if any statement in the block cannot fall through, the block as a
                // whole cannot fall through to its successor. This handles LowerSwitchToBlock output
                // where the leading switch terminates and the trailing exit label is unreachable.
                foreach (var stmt in block.Statements)
                    if (!CanStatementFallThrough(stmt))
                        return false;
                return true;
            case SwitchStatementSyntax sw:
                return CanSwitchFallThrough(sw);
            case IfStatementSyntax iff:
                // An if without an else branch can always fall through (the missing else
                // branch simply continues to the next statement). With an else, the if can
                // fall through iff at least one branch can fall through.
                if (iff.Else == null)
                    return true;
                return CanStatementFallThrough(iff.Statement) || CanStatementFallThrough(iff.Else.Statement);
            case LabeledStatementSyntax labeled:
                return CanStatementFallThrough(labeled.Statement);
            case TryStatementSyntax tryStmt:
                return CanTryStatementFallThrough(tryStmt);
            case EmptyStatementSyntax:
                return true;
            case BreakStatementSyntax:
            case ContinueStatementSyntax:
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case GotoStatementSyntax:
                return false;
            default:
                return true;
        }
    }

    /// <summary>在每层循环体末尾、循环后和嵌套 switch 后追加 if(__exit) break;。
    /// 循环体末尾的检查：StateAssignBreak 的 break 仅退出内层 switch，
    /// 不退出本层循环；若无此检查，do-while 的 while(cond) 在 __exit=true 后仍被求值，
    /// 有副作用的条件会被错误执行。末尾检查使 break 能进一步跳出本层循环。
    /// switch 后检查：goto 位于循环内嵌 switch 时，StateAssignBreak 的 break 先退出 switch；
    /// 必须在执行 switch 后续 sibling 语句前传播 __exit，否则这些语句会被错误执行。
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

        /// <summary>在循环体末尾追加 if(__exit) break;。若 body 已是 Block 则追加到其语句列表，否则包成 Block。
        /// 若循环体不会贯穿到末尾（例如 switch 的所有 case 都终止），则不追加，避免生成不可达代码。</summary>
        private static StatementSyntax AppendExitCheckToBody(StatementSyntax body)
        {
            if (!CanStatementFallThrough(body))
                return body;
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

        public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
        {
            var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;
            // Only append an exit check after a switch if the switch can actually
            // fall through. If every section ends with a terminating statement
            // (continue/break/return/throw/goto) the exit check would be unreachable.
            if (!CanSwitchFallThrough(visited))
                return visited;
            return SyntaxFactory.Block(visited, ExitCheck());
        }
    }

    private sealed class NamedLoopExitInserter : CSharpSyntaxRewriter
    {
        private readonly ExpressionSyntax _exitFlag;

        internal NamedLoopExitInserter(string exitName)
        {
            _exitFlag = SyntaxFactory.IdentifierName(exitName);
        }

        private StatementSyntax ExitCheck()
            => SyntaxFactory.IfStatement(_exitFlag, SyntaxFactory.BreakStatement());

        private StatementSyntax WrapLoop(StatementSyntax loop)
            => SyntaxFactory.Block(loop, ExitCheck());

        private StatementSyntax AppendExitCheckToBody(StatementSyntax body)
        {
            if (!CanStatementFallThrough(body))
                return body;
            if (body is BlockSyntax block)
                return block.WithStatements(block.Statements.Add(ExitCheck()));
            return SyntaxFactory.Block(body, ExitCheck());
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            var withExit = visited.WithStatement(AppendExitCheckToBody(visited.Statement));
            return WrapLoop(withExit);
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            var withExit = visited.WithStatement(AppendExitCheckToBody(visited.Statement));
            return WrapLoop(withExit);
        }

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            var withExit = visited.WithStatement(AppendExitCheckToBody(visited.Statement));
            return WrapLoop(withExit);
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            var withExit = visited.WithStatement(AppendExitCheckToBody(visited.Statement));
            return WrapLoop(withExit);
        }

        public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
        {
            var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;
            // Only append an exit check after a switch if the switch can actually
            // fall through. If every section ends with a terminating statement
            // (continue/break/return/throw/goto) the exit check would be unreachable.
            if (!CanSwitchFallThrough(visited))
                return visited;
            return SyntaxFactory.Block(visited, ExitCheck());
        }
    }
}
