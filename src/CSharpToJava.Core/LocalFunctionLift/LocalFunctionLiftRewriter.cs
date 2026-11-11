using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// CSharpSyntaxRewriter that performs the actual syntax transformations:
/// 1. Deletes local function declarations (from their containing block)
/// 2. Rewrites call sites: renames + prepends capture arguments
/// 3. Renames captured variable references inside hoisted method bodies
/// 4. Inserts new MethodDeclarationSyntax into the host type
/// </summary>
internal sealed class LocalFunctionLiftRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly List<LiftPlan> _plans;
    private readonly LocalFunctionLiftOptions _options;
    private readonly LocalFunctionLiftStatistics _stats;

    private readonly Dictionary<ISymbol, LiftPlan> _symbolToPlan;
    private readonly Dictionary<string, LiftPlan> _nameToPlan;
    private readonly Dictionary<TypeDeclarationSyntax, List<(LiftPlan Plan, MethodDeclarationSyntax Method)>> _methodsToInsert;
    private readonly Dictionary<LiftPlan, List<int>> _callSitePositions = new();
    private readonly Dictionary<SyntaxTree, ConditionalDirectiveLayout> _conditionalLayouts = new();
    private readonly List<LiftPlan> _unremovablePlans = new();
    /// <summary>
    /// 需要以 StrongBox 闭包传递的被捕获变量：符号 -> 元素类型显示。
    /// 命中时把声明处改写为同名 StrongBox，并把该成员内所有引用改为 `x.Value`。
    /// </summary>
    private readonly Dictionary<ISymbol, string> _holderSymbols =
        new(SymbolEqualityComparer.Default);
    /// <summary>
    /// 声明处无初始化器、但被提升后的方法以 <c>ref</c> 形参写入的局部变量：
    /// 声明补 <c>= default(T)</c>，否则调用点的 <c>ref</c> 实参违反确定赋值（CS0165）。
    /// </summary>
    private readonly HashSet<ISymbol> _seededSymbols =
        new(SymbolEqualityComparer.Default);
    /// <summary>
    /// 正在为其生成提升方法体的 plan；仅在 BuildMethodDeclaration 遍历函数体时为非 null。
    /// 用于按符号改写捕获引用（避免同名形参/局部被误改）。
    /// </summary>
    private LiftPlan? _hoistingPlan;
    /// <summary>
    /// 条件编译区（<c>DisabledTextTrivia</c>）里的精确标识符替换：
    /// trivia 原树起始位置 -&gt; 替换列表。只有原名与宿主成员冲突、必须改名时才有条目。
    /// </summary>
    private readonly Dictionary<int, List<DisabledTextIdentifierRename>> _disabledTextRenames =
        new();

    public bool Changed { get; private set; }

    /// <summary>
    /// V-09 atomicity guard: non-skipped lift plans whose declaration lives in
    /// a statement container this rewriter cannot remove from (anything other
    /// than BlockSyntax / SwitchSectionSyntax). The preprocessor must roll the
    /// whole file back when this is non-empty — otherwise the class-level
    /// method and rewritten call sites land while the original declaration
    /// stays, which is exactly the half-rewritten tree shape V-09 caught.
    /// </summary>
    public IReadOnlyList<LiftPlan> UnremovablePlans => _unremovablePlans;

    public LocalFunctionLiftRewriter(
        SemanticModel model,
        List<LiftPlan> plans,
        LocalFunctionLiftOptions options,
        LocalFunctionLiftStatistics stats)
    {
        _model = model;
        _plans = plans;
        _options = options;
        _stats = stats;
        _symbolToPlan = new Dictionary<ISymbol, LiftPlan>(SymbolEqualityComparer.Default);
        _nameToPlan = new Dictionary<string, LiftPlan>(StringComparer.Ordinal);
        _methodsToInsert = new Dictionary<TypeDeclarationSyntax, List<(LiftPlan Plan, MethodDeclarationSyntax Method)>>();

        foreach (var plan in plans.Where(p => !p.Skipped))
        {
            if (plan.Symbol != null)
                _symbolToPlan[plan.Symbol] = plan;
            _nameToPlan[plan.Node.Identifier.Text] = plan;
            foreach (var capture in plan.Captures)
            {
                if (capture.UseHolder && capture.HolderElementTypeDisplay != null)
                {
                    _holderSymbols[capture.Symbol] = capture.HolderElementTypeDisplay;
                }
                else if (capture.IsWritten && NeedsDefaultInitializer(capture.Symbol))
                {
                    _seededSymbols.Add(capture.Symbol);
                }
            }

            foreach (var rename in plan.DisabledTextRenames)
            {
                if (!_disabledTextRenames.TryGetValue(rename.TriviaPosition, out var renames))
                {
                    renames = new List<DisabledTextIdentifierRename>();
                    _disabledTextRenames[rename.TriviaPosition] = renames;
                }
                renames.Add(rename);
            }
        }
    }

    /// <summary>
    /// 条件编译区是不透明文本，普通节点重写无法触及；按分析阶段记录的
    /// trivia 位置与区内偏移做精确标识符替换（仅用于与宿主成员冲突而必须改名的情形）。
    /// </summary>
    public override SyntaxToken VisitToken(SyntaxToken token)
    {
        var visited = base.VisitToken(token);
        if (_disabledTextRenames.Count == 0)
            return visited;

        var leading = RewriteDisabledText(visited.LeadingTrivia);
        var trailing = RewriteDisabledText(visited.TrailingTrivia);
        if (leading is { } newLeading)
            visited = visited.WithLeadingTrivia(newLeading);
        if (trailing is { } newTrailing)
            visited = visited.WithTrailingTrivia(newTrailing);
        return visited;
    }

    private SyntaxTriviaList? RewriteDisabledText(SyntaxTriviaList triviaList)
    {
        SyntaxTriviaList? result = null;
        foreach (var trivia in triviaList)
        {
            if (!trivia.IsKind(SyntaxKind.DisabledTextTrivia)
                || !_disabledTextRenames.TryGetValue(trivia.SpanStart, out var renames))
            {
                continue;
            }

            var text = trivia.ToString();
            foreach (var rename in renames.OrderByDescending(static r => r.Offset))
            {
                text = string.Concat(
                    text.AsSpan(0, rename.Offset),
                    rename.NewName,
                    text.AsSpan(rename.Offset + rename.Length));
            }

            Changed = true;
            result ??= triviaList;
            result = result.Value.Replace(trivia, SyntaxFactory.DisabledText(text));
        }

        return result;
    }

    public void PrepareMethodDeclarations()
    {
        var byHost = _plans
            .Where(p => !p.Skipped && p.HostType != null)
            .GroupBy(p => p.HostType!);

        foreach (var group in byHost)
        {
            var hostType = group.Key;
            var methods = new List<(LiftPlan Plan, MethodDeclarationSyntax Method)>();
            foreach (var plan in group)
            {
                var method = BuildMethodDeclaration(plan);
                methods.Add((plan, method));
                _callSitePositions[plan] = CollectCallSitePositions(plan);
                _stats.LocalFunctionsLifted++;
                foreach (var cap in plan.Captures)
                {
                    if (cap.ByRef)
                        _stats.CapturesByRef++;
                    else
                        _stats.CapturesByValue++;
                }
            }
            _methodsToInsert[hostType] = methods;
        }
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        => VisitTypeDeclarationCore(node, base.VisitClassDeclaration(node));

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        => VisitTypeDeclarationCore(node, base.VisitStructDeclaration(node));

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        => VisitTypeDeclarationCore(node, base.VisitInterfaceDeclaration(node));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
        => VisitTypeDeclarationCore(node, base.VisitRecordDeclaration(node));

    private TypeDeclarationSyntax? VisitTypeDeclarationCore(
        TypeDeclarationSyntax originalNode, SyntaxNode? visitedNode)
    {
        if (visitedNode is not TypeDeclarationSyntax visited)
            return null;

        if (_methodsToInsert.TryGetValue(originalNode, out var methods))
        {
            var members = visited.Members.ToList();
            var layout = GetConditionalLayout(originalNode.SyntaxTree);
            var gapStacks = GetGapStacks(originalNode, layout);

            // 每个提升方法必须与它的“声明点 + 所有调用点”处于相同的条件编译
            // 可见域。旧逻辑固定插在“最后一个字段之后”：若那个字段恰好在
            // #if DEBUG 之类的区域内，方法就被关进该区域，而调用点在区域外
            // 看不到它（CS0103）。这里按条件指令栈逐方法选择插入点。
            var insertions = new List<(int Index, int Order, MethodDeclarationSyntax Method)>();
            for (var order = 0; order < methods.Count; order++)
            {
                var (plan, method) = methods[order];
                var target = GetVisibleConditionalStack(plan, layout);
                var index = SelectInsertionIndex(originalNode, gapStacks, target);
                insertions.Add((index, order, method));
            }

            foreach (var insertion in insertions
                         .OrderByDescending(static i => i.Index)
                         .ThenByDescending(static i => i.Order))
            {
                var method = insertion.Method;
                if (insertion.Index > 0 || insertion.Order > 0)
                {
                    var existingTrivia = method.GetLeadingTrivia();
                    var newTrivia = SyntaxFactory.TriviaList(
                        SyntaxFactory.CarriageReturnLineFeed,
                        SyntaxFactory.CarriageReturnLineFeed);
                    newTrivia = newTrivia.AddRange(existingTrivia);
                    method = method.WithLeadingTrivia(newTrivia);
                }
                members.Insert(insertion.Index, method);
            }

            visited = visited.WithMembers(SyntaxFactory.List(members));
            Changed = true;
        }

        return visited;
    }

    /// <summary>
    /// 记录某个局部函数的所有调用/引用位置（用于求条件编译可见域交集）。
    /// 只需在其声明所在的语句容器内查找；局部函数的作用域不会超出该容器。
    /// </summary>
    private List<int> CollectCallSitePositions(LiftPlan plan)
    {
        var positions = new HashSet<int>();
        var scope = plan.Node.Parent;
        if (scope == null)
            return new List<int>();

        var originalName = plan.Node.Identifier.Text;
        foreach (var node in scope.DescendantNodes())
        {
            string? name = node switch
            {
                InvocationExpressionSyntax { Expression: IdentifierNameSyntax id } => id.Identifier.Text,
                InvocationExpressionSyntax { Expression: GenericNameSyntax gn } => gn.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                GenericNameSyntax gn => gn.Identifier.Text,
                _ => null,
            };
            if (name != originalName)
                continue;

            var symbol = node is InvocationExpressionSyntax invocation
                ? _model.GetSymbolInfo(invocation).Symbol
                : _model.GetSymbolInfo(node).Symbol;
            if (symbol is not IMethodSymbol method)
                continue;
            if (SymbolEqualityComparer.Default.Equals(method, plan.Symbol)
                || SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, plan.Symbol))
            {
                positions.Add(node.SpanStart);
            }
        }

        return positions.ToList();
    }

    /// <summary>
    /// 计算方法的条件编译可见域：声明点与所有调用点的指令栈的公共前缀。
    /// 栈是嵌套路径，交集就是公共前缀；方法放在该前缀对应的位置，才能被
    /// 每个调用点看到，同时不会比声明点更“宽”地泄漏到其它配置。
    /// </summary>
    private List<string> GetVisibleConditionalStack(LiftPlan plan, ConditionalDirectiveLayout layout)
    {
        var stack = layout.StackAt(plan.Node.SpanStart).ToList();

        if (_callSitePositions.TryGetValue(plan, out var positions))
        {
            foreach (var position in positions)
            {
                var callStack = layout.StackAt(position);
                var common = 0;
                while (common < stack.Count
                       && common < callStack.Count
                       && stack[common] == callStack[common])
                {
                    common++;
                }

                if (common < stack.Count)
                    stack.RemoveRange(common, stack.Count - common);
                if (stack.Count == 0)
                    break;
            }
        }

        return stack;
    }

    /// <summary>宿主类型每个成员边界（含末尾）处的条件编译指令栈。</summary>
    private static List<IReadOnlyList<string>> GetGapStacks(
        TypeDeclarationSyntax type, ConditionalDirectiveLayout layout)
    {
        var stacks = new List<IReadOnlyList<string>>(type.Members.Count + 1);
        foreach (var member in type.Members)
            stacks.Add(layout.StackAt(member.FullSpan.Start));
        stacks.Add(layout.StackAt(type.CloseBraceToken.FullSpan.Start));
        return stacks;
    }

    /// <summary>
    /// 选择满足目标指令栈的成员边界。优先沿用“最后一个字段之后”的位置；
    /// 没有精确匹配时退到目标栈的最深可用前缀（保证方法在调用点可见）。
    /// </summary>
    private static int SelectInsertionIndex(
        TypeDeclarationSyntax type,
        List<IReadOnlyList<string>> gapStacks,
        List<string> target)
    {
        var exact = new List<int>();
        for (var i = 0; i < gapStacks.Count; i++)
        {
            if (StacksEqual(gapStacks[i], target))
                exact.Add(i);
        }

        if (exact.Count > 0)
        {
            for (var i = exact.Count - 1; i >= 0; i--)
            {
                var index = exact[i];
                if (index > 0 && type.Members[index - 1] is FieldDeclarationSyntax)
                    return index;
            }
            return exact[^1];
        }

        var best = -1;
        var bestPrefixLength = -1;
        for (var i = 0; i < gapStacks.Count; i++)
        {
            var gap = gapStacks[i];
            if (!IsPrefix(gap, target))
                continue;
            if (gap.Count > bestPrefixLength)
            {
                best = i;
                bestPrefixLength = gap.Count;
            }
            else if (gap.Count == bestPrefixLength && i > best)
            {
                best = i;
            }
        }

        return best >= 0 ? best : 0;
    }

    private static bool StacksEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count && left.SequenceEqual(right);

    private static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> full)
    {
        if (prefix.Count > full.Count)
            return false;
        for (var i = 0; i < prefix.Count; i++)
        {
            if (prefix[i] != full[i])
                return false;
        }
        return true;
    }

    private ConditionalDirectiveLayout GetConditionalLayout(SyntaxTree tree)
    {
        if (!_conditionalLayouts.TryGetValue(tree, out var layout))
        {
            layout = ConditionalDirectiveLayout.Create(tree);
            _conditionalLayouts[tree] = layout;
        }
        return layout;
    }

    /// <summary>
    /// 把源码行中的 #if/#elif/#else/#endif 映射为任意偏移处的条件编译指令栈。
    /// 栈帧用 "指令偏移:分支序号" 标识，分支区分同一个 #if 的不同分支。
    /// </summary>
    private sealed class ConditionalDirectiveLayout
    {
        private enum DirectiveKind
        {
            If,
            Elif,
            Else,
            EndIf,
        }

        private readonly record struct DirectiveEvent(int Position, DirectiveKind Kind);

        private readonly List<DirectiveEvent> _events;
        private readonly Dictionary<int, IReadOnlyList<string>> _cache = new();

        private ConditionalDirectiveLayout(List<DirectiveEvent> events)
        {
            _events = events;
        }

        public static ConditionalDirectiveLayout Create(SyntaxTree tree)
        {
            var events = new List<DirectiveEvent>();

            // 直接用语法树里的指令 trivia，避免把多行字符串中以 #if 开头的
            // 文本误判为预处理指令。非活动分支内的嵌套指令属于
            // DisabledTextTrivia，本身不需要入栈（活动位置不会落在其中）。
            foreach (var trivia in tree.GetRoot().DescendantTrivia())
            {
                if (!trivia.IsDirective)
                    continue;

                DirectiveKind kind;
                switch (trivia.GetStructure())
                {
                    case IfDirectiveTriviaSyntax:
                        kind = DirectiveKind.If;
                        break;
                    case ElifDirectiveTriviaSyntax:
                        kind = DirectiveKind.Elif;
                        break;
                    case ElseDirectiveTriviaSyntax:
                        kind = DirectiveKind.Else;
                        break;
                    case EndIfDirectiveTriviaSyntax:
                        kind = DirectiveKind.EndIf;
                        break;
                    default:
                        continue;
                }

                events.Add(new DirectiveEvent(trivia.SpanStart, kind));
            }

            return new ConditionalDirectiveLayout(events);
        }

        public IReadOnlyList<string> StackAt(int position)
        {
            if (_cache.TryGetValue(position, out var cached))
                return cached;

            var stack = new List<(int Id, int Branch)>();
            foreach (var directive in _events)
            {
                if (directive.Position >= position)
                    break;

                switch (directive.Kind)
                {
                    case DirectiveKind.If:
                        stack.Add((directive.Position, 0));
                        break;
                    case DirectiveKind.Elif:
                    case DirectiveKind.Else:
                        if (stack.Count > 0)
                        {
                            var top = stack[^1];
                            stack[^1] = (top.Id, top.Branch + 1);
                        }
                        break;
                    case DirectiveKind.EndIf:
                        if (stack.Count > 0)
                            stack.RemoveAt(stack.Count - 1);
                        break;
                }
            }

            var key = stack.Select(static frame => $"{frame.Id}:{frame.Branch}").ToList();
            _cache[position] = key;
            return key;
        }
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        // Before visiting, record which statement indices in the original
        // block are non-skipped local functions that should be removed.
        // We must use the original (pre-visit) statements because after
        // base.VisitBlock returns, child nodes may have been rewritten
        // (e.g. call sites renamed), causing SpanStart to become 0 for
        // newly constructed nodes. Using the original node reference
        // for identity comparison is the most reliable approach.
        var nonSkippedNodes = new HashSet<SyntaxNode>(
            _plans.Where(p => !p.Skipped)
                  .Select(p => (SyntaxNode)p.Node));

        // Also build a set of SpanStart values from the original block's
        // local function statements as a fallback for nodes that survive
        // without rewriting (same reference).
        var indicesToRemove = new HashSet<int>();
        for (var i = 0; i < node.Statements.Count; i++)
        {
            if (node.Statements[i] is LocalFunctionStatementSyntax localFn
                && nonSkippedNodes.Contains(localFn))
            {
                indicesToRemove.Add(i);
            }
        }

        // First, visit children (call sites get rewritten inside this block).
        var visited = (BlockSyntax?)base.VisitBlock(node);
        if (visited == null)
            return null;

        if (indicesToRemove.Count == 0)
            return visited;

        // If no statements were removed/added by visiting (count is same),
        // we can safely use the original indices.
        if (visited.Statements.Count == node.Statements.Count)
        {
            var newStatements = new List<StatementSyntax>();
            for (var i = 0; i < visited.Statements.Count; i++)
            {
                if (indicesToRemove.Contains(i))
                {
                    Changed = true;
                    continue;
                }
                newStatements.Add(visited.Statements[i]);
            }
            return visited.WithStatements(SyntaxFactory.List(newStatements));
        }

        // Fallback: match by SpanStart (works when nodes were not rewritten)
        var nonSkippedKeys = new HashSet<(int start, int length)>(
            _plans.Where(p => !p.Skipped)
                  .Select(p => (p.Node.SpanStart, p.Node.Span.Length)));

        var fallbackStatements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
        {
            if (statement is LocalFunctionStatementSyntax localFn)
            {
                var key = (localFn.SpanStart, localFn.Span.Length);
                if (nonSkippedKeys.Contains(key))
                {
                    Changed = true;
                    continue;
                }
            }
            fallbackStatements.Add(statement);
        }

        if (fallbackStatements.Count != visited.Statements.Count)
        {
            visited = visited.WithStatements(SyntaxFactory.List(fallbackStatements));
        }

        return visited;
    }

    // V-09: local functions declared directly inside a switch section (the
    // section's statement list, NOT a nested BlockSyntax) were lifted — the
    // class-level method was generated and every call site was rewritten —
    // but the original declaration survived, producing a half-rewritten tree
    // that only the strict residual count caught (rt10 JsonElement.DeepEquals).
    // Mirror the VisitBlock removal channel for SwitchSectionSyntax.Statements.
    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var nonSkippedNodes = new HashSet<SyntaxNode>(
            _plans.Where(p => !p.Skipped)
                  .Select(p => (SyntaxNode)p.Node));

        var indicesToRemove = new HashSet<int>();
        for (var i = 0; i < node.Statements.Count; i++)
        {
            if (node.Statements[i] is LocalFunctionStatementSyntax localFn
                && nonSkippedNodes.Contains(localFn))
            {
                indicesToRemove.Add(i);
            }
        }

        var visited = (SwitchSectionSyntax?)base.VisitSwitchSection(node);
        if (visited == null)
            return null;

        if (indicesToRemove.Count == 0)
            return visited;

        if (visited.Statements.Count == node.Statements.Count)
        {
            var newStatements = new List<StatementSyntax>();
            for (var i = 0; i < visited.Statements.Count; i++)
            {
                if (indicesToRemove.Contains(i))
                {
                    Changed = true;
                    continue;
                }
                newStatements.Add(visited.Statements[i]);
            }
            return visited.WithStatements(SyntaxFactory.List(newStatements));
        }

        // Fallback: match by SpanStart (same rationale as VisitBlock).
        var nonSkippedKeys = new HashSet<(int start, int length)>(
            _plans.Where(p => !p.Skipped)
                  .Select(p => (p.Node.SpanStart, p.Node.Span.Length)));

        var fallbackStatements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
        {
            if (statement is LocalFunctionStatementSyntax localFn)
            {
                var key = (localFn.SpanStart, localFn.Span.Length);
                if (nonSkippedKeys.Contains(key))
                {
                    Changed = true;
                    continue;
                }
            }
            fallbackStatements.Add(statement);
        }

        if (fallbackStatements.Count != visited.Statements.Count)
        {
            visited = visited.WithStatements(SyntaxFactory.List(fallbackStatements));
        }

        return visited;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var visited = (LocalFunctionStatementSyntax?)base.VisitLocalFunctionStatement(node);

        // V-09 atomicity: for a non-skipped plan the class-level method has
        // already been generated (PrepareMethodDeclarations) and call sites
        // are being rewritten, so this declaration MUST live in a container a
        // removal channel above (VisitBlock / VisitSwitchSection) can strip it
        // from. Anything else (top-level statements, future syntax surfaces)
        // would land the rewrite half-done: new method + renamed calls but the
        // original declaration still in the tree — exactly the strict residual
        // failure shape V-09 caught on rt10 (JsonElement.DeepEquals). Record
        // the plan; the preprocessor rolls the whole file back when non-empty.
        if (_nameToPlan.TryGetValue(node.Identifier.Text, out var plan)
            && !plan.Skipped
            && ReferenceEquals(plan.Node, node)
            && FindRemovableStatementContainer(node) is not (BlockSyntax or SwitchSectionSyntax))
        {
            _unremovablePlans.Add(plan);
        }

        return visited;
    }

    private static SyntaxNode? FindRemovableStatementContainer(LocalFunctionStatementSyntax localFn)
    {
        // Walk outward to the nearest statement-list owner. Wrapping statements
        // (if/for/lock bodies) put the local function in an inner BlockSyntax,
        // which the VisitBlock channel already handles.
        for (var current = localFn.Parent; current != null; current = current.Parent)
        {
            if (current is BlockSyntax or SwitchSectionSyntax or CompilationUnitSyntax)
                return current;
            if (current is StatementSyntax)
                continue;
            return current;
        }
        return null;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node);
        if (visited == null)
            return null;

        LiftPlan? plan = null;

        if (visited.Expression is IdentifierNameSyntax id)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var p))
            {
                plan = p;
            }
            else if (symbol is IMethodSymbol method
                     && !method.Equals(method.OriginalDefinition, SymbolEqualityComparer.Default)
                     && _symbolToPlan.TryGetValue(method.OriginalDefinition, out var p2))
            {
                plan = p2;
            }
            else if (symbol == null && _nameToPlan.TryGetValue(id.Identifier.Text, out var p3))
            {
                plan = p3;
            }
        }
        else if (visited.Expression is GenericNameSyntax gn)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var p))
            {
                plan = p;
            }
            else if (symbol is IMethodSymbol method
                     && !method.Equals(method.OriginalDefinition, SymbolEqualityComparer.Default)
                     && _symbolToPlan.TryGetValue(method.OriginalDefinition, out var p2))
            {
                plan = p2;
            }
            else if (symbol == null && _nameToPlan.TryGetValue(gn.Identifier.Text, out var p3))
            {
                plan = p3;
            }
        }

        if (plan == null)
            return visited;

        var newExpression = RewriteCallExpression(visited.Expression, plan);

        // If the hoisted method needs explicit type arguments (because it uses
        // enclosing method type parameters), supply them at the call site. The
        // lifted method declares its own parameters first, then the carried
        // enclosing ones, so an explicit list must be extended in that order —
        // otherwise the call site arity disagrees with the declaration (S-05 ③).
        if (plan.NeedsExplicitTypeArguments)
        {
            var enclosingArgs = plan.EnclosingTypeParams
                .Select(tp => SyntaxFactory.ParseTypeName(tp))
                .ToList();
            if (enclosingArgs.Count > 0)
            {
                if (newExpression is IdentifierNameSyntax)
                {
                    newExpression = SyntaxFactory.GenericName(
                        SyntaxFactory.Identifier(plan.NewName),
                        SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(enclosingArgs)));
                }
                else if (newExpression is GenericNameSyntax genericName)
                {
                    newExpression = genericName.WithTypeArgumentList(
                        SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(
                            genericName.TypeArgumentList.Arguments.Concat(enclosingArgs))));
                }
            }
        }

        var newArgs = RewriteCallArguments(visited.ArgumentList, plan);

        Changed = true;
        return visited.WithExpression(newExpression).WithArgumentList(newArgs);
    }

    private static ExpressionSyntax RewriteCallExpression(ExpressionSyntax expression, LiftPlan plan)
    {
        if (expression is IdentifierNameSyntax id)
        {
            return SyntaxFactory.IdentifierName(plan.NewName).WithTriviaFrom(id);
        }
        if (expression is GenericNameSyntax gn)
        {
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier(plan.NewName),
                gn.TypeArgumentList).WithTriviaFrom(gn);
        }
        return expression;
    }

    private ArgumentListSyntax RewriteCallArguments(ArgumentListSyntax argumentList, LiftPlan plan)
    {
        if (plan.Captures.Count == 0)
            return argumentList;

        var newArgs = new List<ArgumentSyntax>();

        foreach (var cap in plan.Captures)
        {
            var argExpr = SyntaxFactory.IdentifierName(GetCaptureArgumentName(cap));
            if (cap.ByRef)
            {
                newArgs.Add(SyntaxFactory.Argument(
                    default,
                    SyntaxFactory.Token(SyntaxKind.RefKeyword),
                    argExpr));
            }
            else
            {
                newArgs.Add(SyntaxFactory.Argument(argExpr));
            }
        }

        foreach (var arg in argumentList.Arguments)
        {
            newArgs.Add(arg);
        }

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs));
    }

    /// <summary>
    /// 捕获实参在当前位置应当使用的名字：
    /// - 正在生成提升方法体（_hoistingPlan 非空）时，用当前 plan 对同一符号的形参名；
    /// - 否则用捕获变量原名（声明处仍是同名局部/holder 变量）。
    /// </summary>
    private string GetCaptureArgumentName(CapturePlan capture)
    {
        if (_hoistingPlan != null
            && FindCapture(_hoistingPlan, capture.Symbol) is { } local)
            return local.ParameterName;
        return capture.OriginalName;
    }

    private static CapturePlan? FindCapture(LiftPlan plan, ISymbol symbol)
        => plan.Captures.FirstOrDefault(c =>
            SymbolEqualityComparer.Default.Equals(c.Symbol, symbol));

    /// <summary>
    /// Holder 捕获的声明改写：<c>T x = init;</c> →
    /// <c>StrongBox&lt;T&gt; x = new StrongBox&lt;T&gt;(init);</c>。
    /// 只在声明语句的全部声明符都是 holder 时改写（分析阶段已保证 holder 的声明
    /// 语句只有一个声明符）；同一语句的多个 holder 共用同一个 T。
    /// </summary>
    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        var visited = (LocalDeclarationStatementSyntax?)base.VisitLocalDeclarationStatement(node);
        if (visited == null)
            return null;

        // 只处理原始语法树里的声明（新生成节点没有符号信息）。
        if (!ReferenceEquals(node.SyntaxTree, _model.SyntaxTree))
            return visited;

        string? elementTypeDisplay = null;
        var allHolders = true;
        foreach (var declarator in node.Declaration.Variables)
        {
            var symbol = _model.GetDeclaredSymbol(declarator);
            if (symbol != null && _holderSymbols.TryGetValue(symbol, out var display))
            {
                elementTypeDisplay ??= display;
                continue;
            }
            allHolders = false;
        }

        if (elementTypeDisplay == null || !allHolders)
            return visited;

        var boxType = SyntaxFactory.ParseTypeName(
            $"global::System.Runtime.CompilerServices.StrongBox<{elementTypeDisplay}>");
        var variables = new List<VariableDeclaratorSyntax>();
        foreach (var declarator in visited.Declaration.Variables)
        {
            var value = declarator.Initializer?.Value
                ?? SyntaxFactory.DefaultExpression(
                    SyntaxFactory.ParseTypeName(elementTypeDisplay));
            var boxed = SyntaxFactory.ObjectCreationExpression(boxType.WithoutTrivia())
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(value))));
            variables.Add(declarator.WithInitializer(
                SyntaxFactory.EqualsValueClause(boxed)));
        }

        Changed = true;
        return visited.WithDeclaration(
            visited.Declaration
                .WithType(boxType)
                .WithVariables(SyntaxFactory.SeparatedList(variables)));
    }

    /// <summary>
    /// 无初始化器的写入型捕获补 <c>= default(T)</c>：提升后的方法以 ref 形参写它，
    /// 调用点的实参必须确定赋值（CS0165）。原代码能通过确定赋值分析，说明读取前
    /// 必经赋值，补默认值只影响确定赋值，不改变运行语义。
    /// </summary>
    public override SyntaxNode? VisitVariableDeclaration(VariableDeclarationSyntax node)
    {
        var visited = (VariableDeclarationSyntax?)base.VisitVariableDeclaration(node);
        if (visited == null || !ReferenceEquals(node.SyntaxTree, _model.SyntaxTree))
            return visited;

        var variables = new List<VariableDeclaratorSyntax>(visited.Variables.Count);
        var changed = false;
        for (var i = 0; i < visited.Variables.Count && i < node.Variables.Count; i++)
        {
            var declarator = visited.Variables[i];
            var symbol = _model.GetDeclaredSymbol(node.Variables[i]);
            if (declarator.Initializer == null
                && symbol != null
                && _seededSymbols.Contains(symbol))
            {
                declarator = declarator.WithInitializer(
                    SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.DefaultExpression(visited.Type.WithoutTrivia())));
                changed = true;
            }
            variables.Add(declarator);
        }

        if (!changed)
            return visited;

        Changed = true;
        return visited.WithVariables(SyntaxFactory.SeparatedList(variables));
    }

    private static bool NeedsDefaultInitializer(ISymbol symbol)
        => symbol.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault() is { Initializer: null };

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        // Try to resolve the symbol for this identifier
        var symbol = _model.GetSymbolInfo(node).Symbol;

        // 正在生成某个提升方法的函数体：捕获引用必须按符号改写成对应的形参名。
        // 名字匹配在这里不可靠——局部函数自己的形参可能与被捕获的外层变量同名
        // （gen/RegexGenerator.Emitter.cs 的 EmitNode/EmitAlternation 都是
        // `RegexNode node` 形参 + 外层 `node` 局部），按名字改会把形参引用也
        // 改成捕获形参，调用点传进来的实参被静默丢掉。
        if (_hoistingPlan != null
            && symbol != null
            && node.Parent is not NameColonSyntax
            && FindCapture(_hoistingPlan, symbol) is { } capture)
        {
            Changed = true;
            var parameterName = SyntaxFactory.IdentifierName(capture.ParameterName)
                .WithTriviaFrom(node);
            if (!capture.UseHolder)
                return parameterName;
            if (CaptureClassification.IsInsideNameOf(node))
                return parameterName;
            return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    parameterName.WithoutTrivia(),
                    SyntaxFactory.IdentifierName("Value"))
                .WithTriviaFrom(node);
        }

        // Holder 捕获：声明处的变量已改成 StrongBox<T>，该成员内所有绑定到它
        // 的引用都要读/写 `.Value`（ref/out 实参变成 ref/out x.Value 也合法）。
        // nameof(x) 保持原名（字符串必须一致）。
        if (symbol != null
            && _holderSymbols.ContainsKey(symbol)
            && node.Parent is not NameColonSyntax
            && !CaptureClassification.IsInsideNameOf(node))
        {
            Changed = true;
            return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(node.Identifier.WithoutTrivia()),
                    SyntaxFactory.IdentifierName("Value"))
                .WithTriviaFrom(node);
        }

        LiftPlan? plan = null;

        if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var p1))
        {
            plan = p1;
        }
        else if (symbol is IMethodSymbol method
                 && !method.Equals(method.OriginalDefinition, SymbolEqualityComparer.Default)
                 && _symbolToPlan.TryGetValue(method.OriginalDefinition, out var p2))
        {
            plan = p2;
        }
        else if (symbol == null && _nameToPlan.TryGetValue(node.Identifier.Text, out var p3))
        {
            plan = p3;
        }

        if (plan == null)
            return base.VisitIdentifierName(node);

        // Skip if this is a member access expression on the left
        // (e.g. this.x or obj.x where x matches a hoisted name)
        if (node.Parent is MemberAccessExpressionSyntax mae && mae.Name == node)
            return base.VisitIdentifierName(node);

        // Skip if inside a NameColon (named argument)
        if (node.Parent is NameColonSyntax)
            return base.VisitIdentifierName(node);

        // Determine if this is an invocation call site or a method group reference.
        // If the parent is an InvocationExpression and this node is the expression
        // being invoked, it's a call site — handled by VisitInvocationExpression.
        if (node.Parent is InvocationExpressionSyntax invocation
            && invocation.Expression == node)
            return base.VisitIdentifierName(node);

        // If the parent is a GenericName, it's handled by VisitInvocationExpression
        // through the GenericName path.
        if (node.Parent is GenericNameSyntax)
            return base.VisitIdentifierName(node);

        // Otherwise, this is a method group / delegate conversion reference.
        // 提升后的方法把捕获变量变成了 ref 参数，因此方法组不再与目标委托类型兼容
        // （CS0123）。有捕获时改写为等价 lambda：(a, b) => name(ref cap, a, b)。
        if (plan.Captures.Count > 0 && !plan.NeedsExplicitTypeArguments)
        {
            var lambda = TryBuildCaptureLambda(node, plan);
            if (lambda != null)
            {
                Changed = true;
                return lambda.WithTriviaFrom(node);
            }
        }

        // Rename it to the hoisted method name.
        Changed = true;
        return SyntaxFactory.IdentifierName(plan.NewName).WithTriviaFrom(node);
    }

    /// <summary>
    /// 把"捕获型局部函数的方法组引用"改写为显式 lambda：目标委托类型的参数逐个透传，
    /// 捕获变量按 ref/值前缀传入。无法确定目标委托签名时返回 null（保留原样）。
    /// </summary>
    private ExpressionSyntax? TryBuildCaptureLambda(IdentifierNameSyntax node, LiftPlan plan)
    {
        var converted = _model.GetTypeInfo(node).ConvertedType;
        var invoke = (converted as INamedTypeSymbol) is { TypeKind: TypeKind.Delegate } delegateType
            ? delegateType.DelegateInvokeMethod
            : null;

        // 语义不可用（例如编译单元里存在 ref/src 重复类型定义）时退回到局部函数自身的
        // 参数表：原始方法组转换能成立，说明它的签名与目标委托兼容，直接透传即可。
        if (invoke == null)
        {
            return TryBuildLambdaFromLocalFunctionSignature(node, plan);
        }

        var arguments = new List<ArgumentSyntax>();
        foreach (var capture in plan.Captures)
        {
            // 调用点看到的是捕获变量的原名（参数名只存在于提升后的方法体内）；
            // 在提升方法体内合成 lambda 时改用当前 plan 的捕获形参名。
            var captureName = SyntaxFactory.IdentifierName(GetCaptureArgumentName(capture));
            arguments.Add(capture.ByRef
                ? SyntaxFactory.Argument(captureName).WithRefKindKeyword(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword))
                : SyntaxFactory.Argument(captureName));
        }

        var lambdaParameters = new List<ParameterSyntax>();
        var invokedArguments = new List<ArgumentSyntax>();
        for (var i = 0; i < invoke.Parameters.Length; i++)
        {
            var parameter = invoke.Parameters[i];
            var parameterName = $"__cs2jArg{i}";
            // Lambda 形参必须与目标委托的 ref/out/in 修饰符严格一致，否则
            // `(a, b, int x) => f(..., out x)` 会报 CS1676。
            lambdaParameters.Add(SyntaxFactory.Parameter(
                    SyntaxFactory.Identifier(parameterName))
                .WithModifiers(BuildLambdaParameterModifiers(parameter.RefKind))
                .WithType(SyntaxFactory.ParseTypeName(parameter.Type.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat))));

            var argument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameterName));
            argument = parameter.RefKind switch
            {
                RefKind.Ref => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
                RefKind.Out => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword)),
                RefKind.In => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.InKeyword)),
                _ => argument,
            };
            invokedArguments.Add(argument);
        }

        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(plan.NewName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                arguments.Concat(invokedArguments))));

        return SyntaxFactory.ParenthesizedLambdaExpression(
            SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(lambdaParameters)),
            invocation);
    }

    /// <summary>按委托形参的 RefKind 生成 lambda 形参的修饰符。</summary>
    private static SyntaxTokenList BuildLambdaParameterModifiers(RefKind refKind)
    {
        return refKind switch
        {
            RefKind.Ref => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
            RefKind.Out => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.OutKeyword)),
            RefKind.In => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InKeyword)),
            RefKind.RefReadOnlyParameter => SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.RefKeyword),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)),
            _ => default,
        };
    }

    private static bool IsInsideNameOf(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax id
                && id.Identifier.Text == "nameof")
                return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>无语义信息时：用局部函数的原参数表构造 lambda。</summary>
    private ExpressionSyntax? TryBuildLambdaFromLocalFunctionSignature(
        IdentifierNameSyntax node,
        LiftPlan plan)
    {
        var parameters = plan.Node.ParameterList?.Parameters ?? default;
        if (parameters.Count == 0 && plan.Node.ParameterList == null)
        {
            return null;
        }

        var lambdaParameters = new List<ParameterSyntax>();
        var invokedArguments = new List<ArgumentSyntax>();
        foreach (var parameter in parameters)
        {
            if (parameter.Type == null)
            {
                return null; // 形参无类型（不该出现）时不做猜测
            }

            lambdaParameters.Add(SyntaxFactory.Parameter(parameter.Identifier.WithoutTrivia())
                .WithModifiers(SyntaxFactory.TokenList(parameter.Modifiers.Where(m =>
                    m.IsKind(SyntaxKind.RefKeyword)
                    || m.IsKind(SyntaxKind.OutKeyword)
                    || m.IsKind(SyntaxKind.InKeyword)
                    || m.IsKind(SyntaxKind.ReadOnlyKeyword))))
                .WithType(parameter.Type.WithoutTrivia()));

            var argument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameter.Identifier));
            argument = parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword))
                ? argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword))
                : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword))
                    ? argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword))
                    : argument;
            invokedArguments.Add(argument);
        }

        var arguments = new List<ArgumentSyntax>();
        foreach (var capture in plan.Captures)
        {
            var captureName = SyntaxFactory.IdentifierName(GetCaptureArgumentName(capture));
            arguments.Add(capture.ByRef
                ? SyntaxFactory.Argument(captureName).WithRefKindKeyword(
                    SyntaxFactory.Token(SyntaxKind.RefKeyword))
                : SyntaxFactory.Argument(captureName));
        }

        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(plan.NewName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                arguments.Concat(invokedArguments))));

        return SyntaxFactory.ParenthesizedLambdaExpression(
            SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(lambdaParameters)),
            invocation);
    }

    private MethodDeclarationSyntax BuildMethodDeclaration(LiftPlan plan)
    {
        var localFn = plan.Node;

        var modifierTokens = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword)
        };
        if (plan.IsStatic)
            modifierTokens.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            modifierTokens.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword)))
            modifierTokens.Add(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
        var modifiers = SyntaxFactory.TokenList(modifierTokens);

        var returnType = localFn.ReturnType ?? SyntaxFactory.PredefinedType(
            SyntaxFactory.Token(SyntaxKind.VoidKeyword));

        var typeParams = BuildTypeParameterList(plan);
        var parameters = BuildParameterList(plan);
        var constraints = BuildConstraintClauses(plan);

        BlockSyntax? body = null;

        var previousHoistingPlan = _hoistingPlan;
        _hoistingPlan = plan;
        try
        {
            if (localFn.Body != null)
            {
                body = (BlockSyntax)Visit(localFn.Body)!;
                body = RenameCapturesInBody(body, plan);
            }
            else if (localFn.ExpressionBody != null)
            {
                var expr = localFn.ExpressionBody.Expression;
                var visitedExpr = (ExpressionSyntax)Visit(expr)!;
                visitedExpr = RenameCapturesInExpression(visitedExpr, plan);

                var isVoid = returnType.IsKind(SyntaxKind.VoidKeyword)
                    || (returnType is PredefinedTypeSyntax pt && pt.Keyword.IsKind(SyntaxKind.VoidKeyword));

                // async 返回 Task/ValueTask 的局部函数，表达式体可以是 void 表达式
                // （cs-text p05: `static async Task ValidateWhitespace(...) =>
                // validator.ValidateWhitespace(await t.ConfigureAwait(false))`，被调用方返回 void）。
                // 这种表达式体在原位置就是「语句」，转成 `return <void 表达式>` 会 CS1997
                // （返回 Task 的异步方法的 return 不得后跟对象表达式）。
                if (!isVoid
                    && _model.GetTypeInfo(expr).ConvertedType?.SpecialType == SpecialType.System_Void)
                {
                    isVoid = true;
                }

                StatementSyntax stmt;
                if (isVoid)
                {
                    stmt = SyntaxFactory.ExpressionStatement(visitedExpr);
                }
                else
                {
                    stmt = SyntaxFactory.ReturnStatement(visitedExpr);
                }
                body = SyntaxFactory.Block(stmt);
            }
        }
        finally
        {
            _hoistingPlan = previousHoistingPlan;
        }

        var leadingTrivia = localFn.GetLeadingTrivia();

        var method = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List<AttributeListSyntax>(),
            SyntaxFactory.TokenList(modifiers),
            returnType,
            null,
            SyntaxFactory.Identifier(plan.NewName),
            typeParams,
            parameters,
            constraints,
            body,
            null,
            default);

        // 局部函数位于 unsafe 方法/类型内部时，unsafe 上下文是隐式的；提升为类级方法后
        // 不再继承该上下文，指针/stackalloc/fixed 等用法必须显式标注 unsafe（否则
        // CS0214）。仅在实际需要时才加，避免 AllowUnsafeBlocks=false 的项目报 CS0227。
        if (!modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword))
            && RequiresUnsafeContext(method))
        {
            var unsafeModifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
            method = method.WithModifiers(SyntaxFactory.TokenList(unsafeModifiers));
        }

        if (leadingTrivia.Count > 0)
            method = method.WithLeadingTrivia(leadingTrivia);

        return method;
    }

    /// <summary>
    /// 提升后的方法是否必须处于 unsafe 上下文：签名或方法体中出现指针类型、函数指针、
    /// <c>-&gt;</c>、stackalloc、fixed、取地址，或对非基元类型做 sizeof。
    /// </summary>
    private static bool RequiresUnsafeContext(MethodDeclarationSyntax method)
    {
        foreach (var node in method.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case PointerTypeSyntax:
                case FunctionPointerTypeSyntax:
                case MemberAccessExpressionSyntax { OperatorToken.RawKind: (int)SyntaxKind.MinusGreaterThanToken }:
                case StackAllocArrayCreationExpressionSyntax:
                case ImplicitStackAllocArrayCreationExpressionSyntax:
                case FixedStatementSyntax:
                case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression }:
                    return true;

                case SizeOfExpressionSyntax sizeOf
                    when sizeOf.Type is not PredefinedTypeSyntax:
                    return true;
            }
        }

        return false;
    }

    private static TypeParameterListSyntax? BuildTypeParameterList(LiftPlan plan)
    {
        // R1b: the unified resolver owns declaration order and shadowing names
        // (own first, then enclosing closure; host type parameters excluded).
        if (plan.TypeParameterPlan is { } resolverPlan)
        {
            if (resolverPlan.Parameters.Count == 0)
                return null;

            return SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(
                resolverPlan.Parameters.Select(static parameter =>
                    SyntaxFactory.TypeParameter(
                        SyntaxFactory.List<AttributeListSyntax>(),
                        default,
                        SyntaxFactory.Identifier(parameter.RenderName)))));
        }

        var localFn = plan.Node;
        var typeParams = new List<TypeParameterSyntax>();

        if (localFn.TypeParameterList != null)
        {
            foreach (var tp in localFn.TypeParameterList.Parameters)
                typeParams.Add(tp);
        }

        var enclosingMethodParams = plan.EnclosingTypeParams;
        if (enclosingMethodParams.Count > 0)
        {
            // The analyzer already resolved which enclosing method type parameters
            // the lifted method must redeclare — used by the body/signature, or
            // reachable through a carried `where` clause (S-05 ②). The same list is
            // what call sites pass as explicit type arguments, so arity always agrees.
            foreach (var tpName in enclosingMethodParams)
            {
                if (typeParams.Any(tp => tp.Identifier.Text == tpName))
                    continue;

                typeParams.Add(SyntaxFactory.TypeParameter(
                    SyntaxFactory.List<AttributeListSyntax>(),
                    default,
                    SyntaxFactory.Identifier(tpName)));
            }
        }

        if (typeParams.Count == 0)
            return null;

        return SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams));
    }

    /// <summary>
    /// Collects the set of type parameter names used in the local function's
    /// body, parameter types, and return type. This is used to determine
    /// which enclosing method type parameters need to be added to the hoisted
    /// method's type parameter list.
    /// </summary>
    private static HashSet<string> GetTypeParametersUsedInLocalFunction(
        LocalFunctionStatementSyntax localFn)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        // Collect from parameter types (including the type node itself,
        // since DescendantNodes does not include the node itself)
        if (localFn.ParameterList != null)
        {
            foreach (var param in localFn.ParameterList.Parameters)
            {
                CollectIdentifierNamesFromType(param.Type, used);
            }
        }

        // Collect from return type
        CollectIdentifierNamesFromType(localFn.ReturnType, used);

        // Collect from body
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body != null)
        {
            foreach (var id in body.DescendantNodes()
                .OfType<IdentifierNameSyntax>())
            {
                used.Add(id.Identifier.Text);
            }
        }

        return used;
    }

    /// <summary>
    /// Collects all IdentifierName text from a type syntax node, including
    /// the node itself if it is an IdentifierNameSyntax. This is needed
    /// because DescendantNodes() does not include the node itself.
    /// </summary>
    private static void CollectIdentifierNamesFromType(
        TypeSyntax? type, HashSet<string> names)
    {
        if (type == null)
            return;
        if (type is IdentifierNameSyntax id)
            names.Add(id.Identifier.Text);
        foreach (var descendant in type.DescendantNodes().OfType<IdentifierNameSyntax>())
            names.Add(descendant.Identifier.Text);
    }

    private ParameterListSyntax BuildParameterList(LiftPlan plan)
    {
        var localFn = plan.Node;
        var parameters = new List<ParameterSyntax>();

        if (!_options.CapturesAtEnd)
        {
            foreach (var cap in plan.Captures)
            {
                parameters.Add(BuildCaptureParameter(cap));
            }
        }

        if (localFn.ParameterList != null)
        {
            foreach (var param in localFn.ParameterList.Parameters)
                parameters.Add(param);
        }

        if (_options.CapturesAtEnd)
        {
            foreach (var cap in plan.Captures)
            {
                parameters.Add(BuildCaptureParameter(cap));
            }
        }

        return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters));
    }

    private static ParameterSyntax BuildCaptureParameter(CapturePlan cap)
    {
        var typeDisplay = cap.UseHolder && cap.HolderElementTypeDisplay != null
            ? $"global::System.Runtime.CompilerServices.StrongBox<{cap.HolderElementTypeDisplay}>"
            : cap.TypeDisplay;
        return SyntaxFactory.Parameter(
            SyntaxFactory.List<AttributeListSyntax>(),
            cap.ByRef
                ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))
                : SyntaxFactory.TokenList(),
            SyntaxFactory.ParseTypeName(typeDisplay),
            SyntaxFactory.Identifier(cap.ParameterName),
            null);
    }

    private static SyntaxList<TypeParameterConstraintClauseSyntax> BuildConstraintClauses(LiftPlan plan)
    {
        // R1b: emit exactly the resolver plan's constraints, never a clause for a
        // parameter the lifted method does not declare. NotExpressible parameters
        // are skipped defensively (the analyzer already conservatively skips them).
        if (plan.TypeParameterPlan is { } resolverPlan)
        {
            var resolved = new List<TypeParameterConstraintClauseSyntax>();
            foreach (var parameter in resolverPlan.Parameters)
            {
                if (parameter.State == SyntheticTypeParameterState.NotExpressible)
                    continue;
                if (parameter.Constraint is { } clause)
                    resolved.Add(RenameConstraintClause(clause, parameter.RenderName));
            }

            return SyntaxFactory.List(resolved);
        }

        var localFn = plan.Node;
        var declared = DeclaredTypeParameterNames(plan);
        var clauses = new List<TypeParameterConstraintClauseSyntax>();

        // S-05 ①: only clauses for parameters the lifted method actually declares.
        // Copying the enclosing method's whole list emitted `where` clauses for
        // parameters that are not in scope (dangling constraint clauses).
        foreach (var clause in localFn.ConstraintClauses)
        {
            if (declared.Contains(clause.Name.Identifier.Text))
                clauses.Add(clause);
        }

        var enclosingMethod = FindEnclosingMethod(plan.Node);
        if (enclosingMethod != null)
        {
            foreach (var clause in enclosingMethod.ConstraintClauses)
            {
                // A local function parameter of the same name shadows the enclosing
                // one (CS0693); the enclosing clause must not be copied.
                if (declared.Contains(clause.Name.Identifier.Text)
                    && !localFn.ConstraintClauses.Any(c => c.Name.Identifier.Text == clause.Name.Identifier.Text))
                {
                    clauses.Add(clause);
                }
            }
        }

        return SyntaxFactory.List(clauses);
    }

    /// <summary>
    /// S-05: the type parameter names the lifted method declares. The analyzer's
    /// <see cref="LiftPlan.EnclosingTypeParams"/> already includes the transitive
    /// closure over constraint references (`where T : IFoo&lt;U&gt;` carries U) and is
    /// what the call sites pass as explicit type arguments, so declaration and
    /// call sites cannot disagree on arity.
    /// </summary>
    private static HashSet<string> DeclaredTypeParameterNames(LiftPlan plan)
    {
        if (plan.TypeParameterPlan is { } resolverPlan)
        {
            return resolverPlan.Parameters
                .Where(static p => p.State != SyntheticTypeParameterState.NotExpressible)
                .Select(static p => p.RenderName)
                .ToHashSet(StringComparer.Ordinal);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tp in plan.Node.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
            names.Add(tp.Identifier.Text);
        foreach (var name in plan.EnclosingTypeParams)
            names.Add(name);
        return names;
    }

    private static TypeParameterConstraintClauseSyntax RenameConstraintClause(
        TypeParameterConstraintClauseSyntax clause,
        string renderName)
        => string.Equals(clause.Name.Identifier.Text, renderName, StringComparison.Ordinal)
            ? clause
            : clause.WithName(
                SyntaxFactory.IdentifierName(renderName).WithTriviaFrom(clause.Name));

    private static MethodDeclarationSyntax? FindEnclosingMethod(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is MethodDeclarationSyntax method)
                return method;
            if (current is TypeDeclarationSyntax)
                return null;
            current = current.Parent;
        }
        return null;
    }

    private static List<string> GetEnclosingMethodTypeParameters(LocalFunctionStatementSyntax localFn)
    {
        var result = new List<string>();
        var method = FindEnclosingMethod(localFn);
        if (method?.TypeParameterList != null)
        {
            foreach (var tp in method.TypeParameterList.Parameters)
                result.Add(tp.Identifier.Text);
        }
        return result;
    }

    private BlockSyntax RenameCapturesInBody(BlockSyntax body, LiftPlan plan)
    {
        var captures = GetFallbackRenamableCaptures(plan);
        if (captures.Count == 0)
            return body;

        var renamer = new CaptureRenamer(captures);
        return (BlockSyntax)renamer.Visit(body)!;
    }

    private ExpressionSyntax RenameCapturesInExpression(ExpressionSyntax expr, LiftPlan plan)
    {
        var captures = GetFallbackRenamableCaptures(plan);
        if (captures.Count == 0)
            return expr;

        var renamer = new CaptureRenamer(captures);
        return (ExpressionSyntax)renamer.Visit(expr)!;
    }

    /// <summary>
    /// 名字匹配的回退重命名只用于符号解析失败（上游错误类型）的标识符。
    /// 捕获名若与函数体里声明的名字（形参/局部/lambda 形参等）重名，
    /// 或有两个捕获同名，按名字无法判断该改哪一个，必须交给 VisitIdentifierName
    /// 的符号匹配处理，否则会把形参引用错误地改成捕获形参。
    /// </summary>
    private static List<CapturePlan> GetFallbackRenamableCaptures(LiftPlan plan)
    {
        if (plan.Captures.Count == 0)
            return new List<CapturePlan>();

        var ambiguous = CollectDeclaredNames(plan.Node);
        foreach (var group in plan.Captures.GroupBy(c => c.OriginalName, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
                ambiguous.Add(group.Key);
        }

        return plan.Captures
            .Where(c => !ambiguous.Contains(c.OriginalName))
            .ToList();
    }

    private static HashSet<string> CollectDeclaredNames(LocalFunctionStatementSyntax localFn)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (localFn.ParameterList != null)
        {
            foreach (var parameter in localFn.ParameterList.Parameters)
                names.Add(parameter.Identifier.Text);
        }

        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return names;

        foreach (var node in body.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case ParameterSyntax parameter:
                    names.Add(parameter.Identifier.Text);
                    break;
                case VariableDeclaratorSyntax declarator:
                    names.Add(declarator.Identifier.Text);
                    break;
                case ForEachStatementSyntax forEach:
                    names.Add(forEach.Identifier.Text);
                    break;
                case SingleVariableDesignationSyntax designation:
                    names.Add(designation.Identifier.Text);
                    break;
                case CatchDeclarationSyntax catchDeclaration
                    when !catchDeclaration.Identifier.IsKind(SyntaxKind.None):
                    names.Add(catchDeclaration.Identifier.Text);
                    break;
                case FromClauseSyntax fromClause:
                    names.Add(fromClause.Identifier.Text);
                    break;
                case LetClauseSyntax letClause:
                    names.Add(letClause.Identifier.Text);
                    break;
                case JoinClauseSyntax joinClause:
                    names.Add(joinClause.Identifier.Text);
                    break;
            }
        }

        return names;
    }

    /// <summary>
    /// Renames captured variable references inside hoisted method bodies.
    /// Uses name-based matching instead of SemanticModel because the body
    /// may have already been rewritten by Visit(), producing nodes that are
    /// not part of the original syntax tree.
    /// </summary>
    private sealed class CaptureRenamer : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _renameMap;

        public CaptureRenamer(List<CapturePlan> captures)
        {
            _renameMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cap in captures)
                _renameMap[cap.OriginalName] = cap.ParameterName;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is NameColonSyntax)
                return node;

            var name = node.Identifier.Text;
            if (_renameMap.TryGetValue(name, out var newName))
            {
                // Skip if this identifier is a member access on the left
                // (e.g. this.x or obj.x where x happens to match a capture name).
                if (node.Parent is MemberAccessExpressionSyntax mae
                    && mae.Name == node)
                    return base.VisitIdentifierName(node);

                return SyntaxFactory.IdentifierName(newName).WithTriviaFrom(node);
            }
            return base.VisitIdentifierName(node);
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            return node;
        }
    }
}
