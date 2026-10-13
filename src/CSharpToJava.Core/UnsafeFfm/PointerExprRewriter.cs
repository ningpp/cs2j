using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R6：指针表达式索引化改写（设计 D7，§4 组件表）。全部基于
/// <see cref="PointerFlowModel"/> 与语义模型，不做文本匹配。
/// M3a 基础族：<c>*p</c>→<c>arr[offset]</c>、<c>p[i]</c>→<c>arr[offset+i]</c>、
/// <c>&amp;arr[i]</c> 取址消解、p-&gt;m/异常强转显式拒绝。
/// M3b 算术族（本文件第二部分，probe 实测 Roslyn 5.0.0 形态后实现）：
/// <list type="bullet">
/// <item><c>p+n</c>/<c>n+p</c>/<c>p-n</c>：模型内传播为同源 int 索引表达式，
/// 不另起体系；元素步长由指向元素类型决定——C# 指针算术本就按元素计数
/// （int* +1 = 索引+1，probe 确认右侧 int 即元素数，无需乘字节步长）；</item>
/// <item><c>p++/++p/p--/--p</c>（含 <c>*p++</c> 组合与 +=/-=）：在索引局部
/// 上自增；索引局部按需引入（参数域在函数体首、局部域在其声明前），自增
/// 表达式原地保留——在 <c>||</c>/<c>&amp;&amp;</c> 条件中不被提前，短路天然保持；</item>
/// <item><c>p-q</c>：仅同源 → int 索引差（probe：C# 原语义为 long，归一后
/// 索引差为 int，long 站点由隐式转换承载）；异源/不可证 → UF020；</item>
/// <item>指针比较：同源 → int 索引比较；不可证 → UF020；<c>p==null</c>
/// 形态为数组空检查，原样保留；</item>
/// <item>指针↔整数强转：仅「同数组差」外套整数强转允许；绝对地址
/// <c>(int)p</c>/<c>(IntPtr)p</c> → UF021；</item>
/// <item>UF022：算术结果不可回填——表达式语句丢弃值、索引局部无处可放
/// （表达式体成员）、指针符号被 lambda 捕获后的自增；</item>
/// <item>R3 域指针（已是 int 索引）参与算术/比较直接按 int 处理；输出无
/// 指针节点，二次跑不重识别（幂等）。</item>
/// </list>
/// </summary>
internal sealed class PointerExprRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly PointerFlowModel _flowModel;
    private readonly IReadOnlyCollection<ISymbol> _loweredSymbols;
    private readonly IReadOnlyDictionary<ISymbol, (string ArrayName, bool ReadOnly)> _loweredArrayMap;
    private readonly PointerMethodCatalog _catalog;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;
    private IndexPlan? _plan;

    /// <summary>当前访问子树所属配对方法栈（栈顶即最近方法计划）。</summary>
    private readonly List<PairedMethodPlan> _pairedStack = new();

    /// <summary>配对 holder 形参符号 → 伴生数组形参名。</summary>
    private readonly Dictionary<ISymbol, string> _holderParameters =
        new(SymbolEqualityComparer.Default);

    /// <summary>
    /// 由 <c>ByRef&lt;T*&gt;.Create</c> 提升出的 holder 局部符号 →
    /// 其捕获游标所在数组名（调用点实参展开需要）。
    /// </summary>
    private readonly Dictionary<ISymbol, string> _holderLocals =
        new(SymbolEqualityComparer.Default);

    public PointerExprRewriter(
        SemanticModel model,
        PointerFlowModel flowModel,
        IReadOnlyCollection<ISymbol> loweredSymbols,
        IReadOnlyDictionary<ISymbol, (string ArrayName, bool ReadOnly)> loweredArrayMap,
        PointerMethodCatalog catalog,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver,
        CompilationUnitSyntax root)
    {
        _model = model;
        _flowModel = flowModel;
        _loweredSymbols = loweredSymbols;
        _loweredArrayMap = loweredArrayMap;
        _catalog = catalog;
        _statistics = statistics;
        _resolver = resolver;
        _plan = BuildPlan(root);
    }

    public List<UnsafeFfmDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    // ---- 配对方法域 ----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var plan = GetCurrentPlan(node);
        if (plan == null) return base.VisitMethodDeclaration(node);

        PushPlan(plan);
        try
        {
            return base.VisitMethodDeclaration(node);
        }
        finally
        {
            _pairedStack.RemoveAt(_pairedStack.Count - 1);
        }
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var plan = GetCurrentPlan(node);
        if (plan == null) return base.VisitLocalFunctionStatement(node);

        PushPlan(plan);
        try
        {
            return base.VisitLocalFunctionStatement(node);
        }
        finally
        {
            _pairedStack.RemoveAt(_pairedStack.Count - 1);
        }
    }

    private void PushPlan(PairedMethodPlan plan)
    {
        foreach (var parameter in plan.Parameters)
        {
            if (parameter.Kind == PairedParamKind.HolderPointer && parameter.Symbol != null)
                _holderParameters[parameter.Symbol] = parameter.ArrayName;
        }

        _pairedStack.Add(plan);
    }

    private PairedMethodPlan? GetCurrentPlan(SyntaxNode node)
    {
        // R5 配对签名改写后声明节点与原始树不再等价：优先读 R5 挂的 SymbolKey 注解。
        if (_catalog.TryGetPlanFromAnnotations(node) is { } annotatedPlan)
            return annotatedPlan;

        var original = _resolver.Resolve(node);
        var symbol = original == null
            ? null
            : _model.GetDeclaredSymbol(original) as IMethodSymbol;
        return symbol == null ? null : _catalog.GetPlan(symbol);
    }

    private PairedMethodPlan? TryGetInvocationPlan(InvocationExpressionSyntax invocation)
    {
        var original = _resolver.Resolve(invocation);
        if (original is not InvocationExpressionSyntax originalInvocation)
            return null;

        var info = _model.GetSymbolInfo(originalInvocation);
        if (info.Symbol is IMethodSymbol direct && _catalog.GetPlan(direct) is { } directPlan)
            return directPlan;

        foreach (var candidate in info.CandidateSymbols)
        {
            if (candidate is IMethodSymbol method
                && _catalog.GetPlan(method) is { } candidatePlan)
            {
                return candidatePlan;
            }
        }

        return null;
    }

    // ---- 配对方法调用点展开 / holder.Create 提升重建 ----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (TryGetInvocationPlan(node) is { } plan)
            return ExpandPairedInvocation(node, plan, out _);

        if (IsHolderCreateInvocation(node))
            return RebuildHolderCreateInvocation(node, out _);

        return base.VisitInvocationExpression(node);
    }

    /// <summary>
    /// 按配对计划逐位重建实参表：PlainPointer 槽把指针实参展开为
    /// 「数组实参 + int 索引实参」；HolderPointer 槽在 holder 实参后插入其
    /// 伴生数组实参；None 槽原样访问。receiver/静态类型限定整体保留。
    /// <paramref name="firstArray"/> 为第一个成功解析的指针槽数组名
    /// （指针返回结果的同源数组载体——配对约定下返回索引相对调用方传入的
    /// 同入参数组空间；调用点一律将结果赋回/返回输出游标，元素访问经该
    /// 游标自身的 flow/降级表解析，不依赖此处数组名）。
    /// </summary>
    private InvocationExpressionSyntax ExpandPairedInvocation(
        InvocationExpressionSyntax invocation,
        PairedMethodPlan plan,
        out string? firstArray)
    {
        firstArray = null;
        var originalArgs = invocation.ArgumentList.Arguments;
        var newArgs = new List<ArgumentSyntax>();

        if (originalArgs.Count != plan.Parameters.Count)
        {
            // 形态偏离（可选实参/params 等不在本族约定内）：不静默吞错，
            // 按普通调用访问，让既有逃逸门/残留闸给出判决。
            return (InvocationExpressionSyntax)base.VisitInvocationExpression(invocation)!;
        }

        for (var i = 0; i < plan.Parameters.Count; i++)
        {
            var slot = plan.Parameters[i];
            var originalArg = originalArgs[i];

            if (slot.Kind == PairedParamKind.None)
            {
                newArgs.Add(originalArg.WithExpression(
                    (ExpressionSyntax)Visit(originalArg.Expression)!));
                continue;
            }

            var rawArg = UnwrapParenthesized(originalArg.Expression);

            if (slot.Kind == PairedParamKind.PlainPointer)
            {
                if (TryBuildIndex(rawArg, out var argArray, out var argIndex, out _))
                {
                    firstArray ??= argArray;
                    newArgs.Add(Argument(ParseExpression(argArray)));
                    newArgs.Add(Argument(Simplify(argIndex)));
                }
                else
                {
                    // 真不可证（外部原生源/别名逃逸）：保留 UF020 判决，文件回滚。
                    AddError(
                        UnsafeFfmDiagnosticCode.UF020,
                        $"pointer argument '{rawArg}' passed to paired method " +
                        $"'{plan.Method.Name}' has no provable array/index origin; the original " +
                        "file was preserved.",
                        rawArg);
                    newArgs.Add(originalArg);
                }

                continue;
            }

            // HolderPointer：holder 实参保留（类型已在声明点/形参点降级为
            // ByRef<int>），其后插入伴生数组实参。
            var holderArray = ResolveHolderArray(rawArg);
            if (holderArray == null)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer holder argument '{rawArg}' passed to paired method " +
                    $"'{plan.Method.Name}' is neither a paired holder parameter nor a ByRef<int> " +
                    "local lifted from a provable cursor; the original file was preserved.",
                    rawArg);
                newArgs.Add(originalArg);
                continue;
            }

            newArgs.Add(originalArg.WithExpression(
                (ExpressionSyntax)Visit(rawArg)!));
            newArgs.Add(Argument(ParseExpression(holderArray)));
        }

        var expression = (ExpressionSyntax)Visit(invocation.Expression)!;
        return invocation
            .WithExpression(expression)
            .WithArgumentList(ArgumentList(SeparatedList(newArgs)));
    }

    /// <summary>
    /// holder 实参（标识符/成员访问）的伴生数组名：当前方法 holder 形参 →
    /// 其伴生数组形参；Create 提升的 holder 局部 → 其捕获游标所在数组。
    /// </summary>
    private string? ResolveHolderArray(ExpressionSyntax holderExpression)
    {
        var symbol = GetExpressionSymbol(holderExpression);
        if (symbol == null) return null;

        if (_holderParameters.TryGetValue(symbol, out var parameterArray))
            return parameterArray;

        if (_holderLocals.TryGetValue(symbol, out var localArray))
            return localArray;

        return null;
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        // C01 提升的 holder 局部：ByRef<T*> __ref = ByRef<T*>.Create<T*>(...)
        // 整体降级为 ByRef<int>，捕获游标改写为其 int 索引文本，并登记
        // _holderLocals 供后续配对调用展开取伴生数组实参。
        if (node.Declaration.Variables.Count == 1
            && node.Declaration.Variables[0] is { } declarator
            && declarator.Initializer?.Value is InvocationExpressionSyntax create
            && IsHolderCreateInvocation(create)
            && TryGetLocalSymbol(declarator) is { } localSymbol
            && PointerMethodCatalog.IsByRefHolder(localSymbol.Type, out _))
        {
            var rebuiltCreate = RebuildHolderCreateInvocation(create, out var capturedArray);
            if (capturedArray == null)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer holder local '{declarator.Identifier.ValueText}' lifts a cursor " +
                    "whose array/index origin is not provable; the original file was preserved.",
                    declarator);
                return node;
            }

            var newType = (TypeSyntax)node.Declaration.Type
                .ReplaceNodes(
                    node.Declaration.Type.DescendantNodes()
                        .OfType<PointerTypeSyntax>().ToList(),
                    (_, _) => PredefinedType(Token(SyntaxKind.IntKeyword)));
            var newDeclarator = declarator.WithInitializer(EqualsValueClause(rebuiltCreate));

            _holderLocals[localSymbol] = capturedArray;
            return node.WithDeclaration(
                node.Declaration.WithType(newType)
                    .WithVariables(SingletonSeparatedList(newDeclarator)));
        }

        return base.VisitLocalDeclarationStatement(node);
    }

    /// <summary>调用表达式是否绑定（含 WrongArity 候选）到 ByRef&lt;T*&gt;.Create。</summary>
    private bool IsHolderCreateInvocation(InvocationExpressionSyntax invocation)
    {
        var original = _resolver.Resolve(invocation);
        if (original is not InvocationExpressionSyntax originalInvocation)
            return false;

        var info = _model.GetSymbolInfo(originalInvocation);
        if (PointerMethodCatalog.IsHolderCreateSymbol(info.Symbol))
            return true;

        foreach (var candidate in info.CandidateSymbols)
        {
            if (PointerMethodCatalog.IsHolderCreateSymbol(candidate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 重建 <c>ByRef&lt;T*&gt;.Create&lt;T*&gt;(() =&gt; p, v =&gt; p = v)</c> 为
    /// <c>ByRef&lt;int&gt;.Create&lt;int&gt;(() =&gt; pIndex, v =&gt; pIndex = v)</c>。
    /// <paramref name="capturedArray"/> 输出捕获游标经 flow/降级表解析出的数组名。
    /// </summary>
    private InvocationExpressionSyntax RebuildHolderCreateInvocation(
        InvocationExpressionSyntax invocation,
        out string? capturedArray)
    {
        capturedArray = null;
        var newArgs = new List<ArgumentSyntax>();
        string? array = null;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            switch (argument.Expression)
            {
                // getter：() => p（无参括号 lambda，表达式体）。
                case ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 0 } getter
                    when getter.Body is ExpressionSyntax getterBody:
                {
                    if (TryMapCapturedCursor(getterBody, out var getterArray, out var mapped))
                    {
                        array = getterArray;
                        newArgs.Add(Argument(getter.WithBody(mapped)));
                        break;
                    }

                    newArgs.Add(argument);
                    break;
                }

                // setter：v => p = v（单参简单 lambda，赋值表达式体）。
                case SimpleLambdaExpressionSyntax setter
                    when setter.Body is AssignmentExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleAssignmentExpression
                    } setterAssignment:
                {
                    if (TryMapCapturedCursor(setterAssignment.Left,
                            out var setterArray, out var mappedLeft))
                    {
                        array = setterArray;
                        var newBody = setterAssignment.WithLeft(mappedLeft);
                        newArgs.Add(Argument(setter.WithBody(newBody)));
                        break;
                    }

                    newArgs.Add(argument);
                    break;
                }

                default:
                    // 访问器为块体/数组+索引等其它形态时按普通子树访问。
                    newArgs.Add(argument.WithExpression(
                        (ExpressionSyntax)Visit(argument.Expression)!));
                    break;
            }
        }

        capturedArray = array;

        // 表达式中所有指针类型实参（ByRef<T*> 与 Create<T*>）统一替换为 int。
        var newExpression = (ExpressionSyntax)invocation.Expression
            .ReplaceNodes(
                invocation.Expression.DescendantNodesAndSelf()
                    .OfType<PointerTypeSyntax>().ToList(),
                (_, _) => PredefinedType(Token(SyntaxKind.IntKeyword)));

        return invocation
            .WithExpression(newExpression)
            .WithArgumentList(ArgumentList(SeparatedList(newArgs)));
    }

    /// <summary>
    /// holder 访问器捕获的游标标识符映射到「数组名 + int 索引表达式」
    /// （FOLDS 形参/shadow/别名 → OffsetText；R3 降级游标 → 同名 int 局部）。
    /// </summary>
    private bool TryMapCapturedCursor(
        ExpressionSyntax raw,
        out string? array,
        out ExpressionSyntax? mapped)
    {
        var expression = UnwrapParenthesized(raw);
        if (GetExpressionSymbol(expression) is { } symbol
            && TryResolveIndexSymbol(symbol, expression.ToString(),
                out var arrayText, out var index, out _))
        {
            array = arrayText;
            mapped = Simplify(index);
            return true;
        }

        array = null;
        mapped = null;
        return false;
    }

    private ILocalSymbol? TryGetLocalSymbol(VariableDeclaratorSyntax declarator)
    {
        var original = _resolver.Resolve(declarator);
        return original == null ? null : _model.GetDeclaredSymbol(original) as ILocalSymbol;
    }

    /// <summary>
    /// 把单个指针位置表达式解析为「数组表达式 + int 索引表达式」。
    /// 覆盖：配对 int 索引形参/FOLDS 局部、R3 降级游标、array-only 形参
    /// （整组数组，索引 0）、指针 ± n、配对指针返回调用。
    /// <paramref name="pairedDomain"/> 为 true 表示根来自配对折叠索引
    /// （形参索引/shadow），允许跨形参数组的比较/相减按入口契约归一。
    /// </summary>
    private bool TryResolveIndex(
        ExpressionSyntax raw,
        out ExpressionSyntax array,
        out ExpressionSyntax index,
        out bool pairedDomain)
    {
        if (TryBuildIndex(raw, out var arrayText, out var indexNode, out pairedDomain))
        {
            array = ParseExpression(arrayText);
            index = indexNode;
            return true;
        }

        array = default!;
        index = default!;
        return false;
    }

    private bool IsFoldsSymbol(ISymbol? symbol)
        => symbol != null
           && _flowModel.TryGet(symbol, out var foldsFlow)
           && foldsFlow is { IsProvable: true, FoldsToOffset: true };

    private bool IsLoweredSymbol(ISymbol? symbol)
        => symbol != null && _loweredArrayMap.ContainsKey(symbol);

    /// <summary>标识符类根符号解析为索引域（FOLDS/降级/array-only flow）。</summary>
    private bool TryResolveIndexSymbol(
        ISymbol? symbol,
        string ownText,
        out string arrayText,
        out ExpressionSyntax index,
        out bool pairedDomain)
    {
        if (IsFoldsSymbol(symbol))
        {
            var flow = _flowModel.TryGet(symbol, out var f) ? f : default;
            arrayText = flow.ArrayExpressionText;
            index = ParseExpression(flow.OffsetText);
            pairedDomain = true;
            return true;
        }

        if (symbol != null && _loweredArrayMap.TryGetValue(symbol, out var lowered))
        {
            arrayText = lowered.ArrayName;
            index = IdentifierName(ownText);
            pairedDomain = false;
            return true;
        }

        if (symbol != null && _flowModel.TryGet(symbol, out var provable) && provable.IsProvable)
        {
            arrayText = provable.ArrayExpressionText;
            // 符号被 p+=n/p++ 复合自增时，R6 已为其合成 int 索引局部，
            // 引用点必须读该局部而非静态偏移 0（旧 M3b 既有约定）。
            if (_plan != null && _plan.Locals.TryGetValue(symbol, out var mutatedLocal))
                index = IdentifierName(mutatedLocal.Name);
            else
                index = LiteralExpression(
                    SyntaxKind.NumericLiteralExpression, Literal(0));
            pairedDomain = false;
            return true;
        }

        arrayText = string.Empty;
        index = default!;
        pairedDomain = false;
        return false;
    }

    // ==================================================================
    // M3b 索引局部计划：扫描当前树，找出需要「int 索引局部」承载自增/
    // 复合赋值的 flow 符号，并确定插入位置与不可放置（UF022）站点。
    // ==================================================================

    private enum BuildStatus { Ok, Unprovable }

    private sealed class IndexLocal
    {
        public required string Name { get; init; }
        public required SyntaxNode MemberSubtree { get; init; }
    }

    private sealed class IndexPlan
    {
        public Dictionary<ISymbol, IndexLocal> Locals { get; } =
            new(SymbolEqualityComparer.Default);

        /// <summary>原始 body block → 需函数体首插入的局部名（参数/表达式块 lambda）。</summary>
        public Dictionary<SyntaxNode, List<string>> MemberStartBodies { get; } = new();

        /// <summary>原始声明语句 → 需在其前插入的局部名（局部符号）。</summary>
        public Dictionary<SyntaxNode, List<string>> AnchorStatements { get; } = new();

        /// <summary>当前树节点：自增/复合站点不可放置索引局部 → UF022。</summary>
        public HashSet<SyntaxNode> BlockedSites { get; } = new();
    }

    private IndexPlan BuildPlan(CompilationUnitSyntax root)
    {
        var plan = new IndexPlan();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case PostfixUnaryExpressionSyntax postfix
                    when postfix.Kind() is SyntaxKind.PostIncrementExpression
                        or SyntaxKind.PostDecrementExpression:
                    ScheduleMutation(postfix.Operand, postfix, plan);
                    break;

                case PrefixUnaryExpressionSyntax prefix
                    when prefix.Kind() is SyntaxKind.PreIncrementExpression
                        or SyntaxKind.PreDecrementExpression:
                    ScheduleMutation(prefix.Operand, prefix, plan);
                    break;

                case AssignmentExpressionSyntax compound
                    when compound.Kind() is SyntaxKind.AddAssignmentExpression
                        or SyntaxKind.SubtractAssignmentExpression:
                    ScheduleMutation(compound.Left, compound, plan);
                    break;
            }
        }

        return plan;
    }

    private void ScheduleMutation(
        ExpressionSyntax rawTarget, SyntaxNode site, IndexPlan plan)
    {
        var target = UnwrapParenthesized(rawTarget);
        if (IsLoweredExpression(target)) return;

        var symbol = GetExpressionSymbol(target);
        if (symbol == null || !_flowModel.TryGet(symbol, out var flow) || !flow.IsProvable)
            return; // 无 flow 的旧指针：在各 Visit 点发 UF020。

        // 配对域折叠游标（形参配对 int 索引/shadow int 局部/别名 int 局部）：
        // 自增直接作用于既有变量，不引入合成索引局部，也不做 lambda 阻塞。
        if (flow.FoldsToOffset)
            return;

        if (plan.Locals.ContainsKey(symbol))
        {
            // 已登记：若站点本身合法即可，阻塞状态在首次登记时已确定。
            return;
        }

        var memberSubtree = site.Ancestors().OfType<SyntaxNode>()
            .First(a => a is MemberDeclarationSyntax
                or SimpleLambdaExpressionSyntax
                or ParenthesizedLambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax);

        // 捕获面：符号在本成员内的任意 lambda/匿名方法中出现 → 自增不做。
        if (IsSymbolUsedInLambda(memberSubtree, symbol))
        {
            plan.BlockedSites.Add(site);
            return;
        }

        var localName = MakeIndexLocalName(symbol.Name, memberSubtree, plan);
        var local = new IndexLocal { Name = localName, MemberSubtree = memberSubtree };
        plan.Locals[symbol] = local;

        PlaceLocal(symbol, local, site, plan);
    }

    private void PlaceLocal(
        ISymbol symbol, IndexLocal local, SyntaxNode site, IndexPlan plan)
    {
        // 参数（含 lambda/索引器参数）：在声明语法所属成员的 body block 首插入。
        if (symbol is IParameterSymbol)
        {
            var declaration = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            var member = declaration?.Parent switch
            {
                BaseParameterListSyntax list => list.Parent,
                _ => null,
            };

            SyntaxNode? body = member switch
            {
                MethodDeclarationSyntax method => method.Body,
                LocalFunctionStatementSyntax localFunction => localFunction.Body,
                OperatorDeclarationSyntax op => op.Body,
                ConversionOperatorDeclarationSyntax conv => conv.Body,
                ConstructorDeclarationSyntax ctor => ctor.Body,
                DestructorDeclarationSyntax destructor => destructor.Body,
                SimpleLambdaExpressionSyntax lambda => lambda.Body,
                ParenthesizedLambdaExpressionSyntax lambda => lambda.Body,
                AnonymousMethodExpressionSyntax anonymous => anonymous.Block,
                _ => null,
            };

            if (body is BlockSyntax block)
            {
                AddToMap(plan.MemberStartBodies, block, local.Name);
                return;
            }

            // 表达式体/索引器参数等：无语句块可放 → 站点 UF022。
            plan.BlockedSites.Add(site);
            return;
        }

        // 局部：在其声明语句前插入（容器为 Block 或 SwitchSection）。
        if (symbol is ILocalSymbol)
        {
            var declarator = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (declarator?.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is { } anchor)
            {
                AddToMap(plan.AnchorStatements, anchor, local.Name);
                return;
            }
        }

        // 其它符号（字段等 R5 已拒绝）：防御性阻塞。
        plan.BlockedSites.Add(site);
    }

    private static void AddToMap(
        Dictionary<SyntaxNode, List<string>> map, SyntaxNode key, string name)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<string>();
            map[key] = list;
        }

        list.Add(name);
    }

    private bool IsSymbolUsedInLambda(SyntaxNode memberSubtree, ISymbol symbol)
    {
        foreach (var lambda in memberSubtree.DescendantNodes().OfType<SyntaxNode>()
                     .Where(n => n is SimpleLambdaExpressionSyntax
                         or ParenthesizedLambdaExpressionSyntax
                         or AnonymousMethodExpressionSyntax))
        {
            foreach (var identifier in lambda.DescendantNodes()
                         .OfType<IdentifierNameSyntax>())
            {
                if (GetExpressionSymbol(identifier) is { } used
                    && SymbolEqualityComparer.Default.Equals(used, symbol))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string MakeIndexLocalName(
        string symbolName, SyntaxNode memberSubtree, IndexPlan plan)
    {
        // 只需在本成员作用域唯一：成员内 token 全部检查即可。不使用进程级
        // 静态集合——那会让二次跑（幂等校验）因名字残留而漂移。
        var tokens = new HashSet<string>(
            memberSubtree.DescendantTokens().Select(t => t.ValueText));
        var candidate = "_" + symbolName + "_index";
        var counter = 2;
        while (tokens.Contains(candidate))
        {
            candidate = "_" + symbolName + "_index" + counter;
            counter++;
        }

        return candidate;
    }

    // ---- 语句容器：插入索引局部声明 ----

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;

        var plan = _plan!;
        if (plan.MemberStartBodies.Count == 0 && plan.AnchorStatements.Count == 0)
            return visited;

        var statements = new List<StatementSyntax>();

        var original = _resolver.Resolve(node);
        if (original != null
            && _plan!.MemberStartBodies.TryGetValue(original, out var memberNames))
        {
            foreach (var name in memberNames.Distinct())
                statements.Add(IndexLocalDeclaration(name));
        }

        foreach (var statement in visited.Statements)
        {
            var statementOriginal = _resolver.Resolve(statement);
            if (statementOriginal != null
                && _plan!.AnchorStatements.TryGetValue(statementOriginal, out var anchorNames))
            {
                foreach (var name in anchorNames.Distinct())
                    statements.Add(IndexLocalDeclaration(name));
            }

            statements.Add(statement);
        }

        return statements.Count == visited.Statements.Count
            ? visited
            : visited.WithStatements(List(statements));
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;

        if (_plan!.AnchorStatements.Count == 0)
            return visited;

        var statements = new List<StatementSyntax>();

        foreach (var statement in visited.Statements)
        {
            var statementOriginal = _resolver.Resolve(statement);
            if (statementOriginal != null
                && _plan!.AnchorStatements.TryGetValue(statementOriginal, out var anchorNames))
            {
                foreach (var name in anchorNames.Distinct())
                    statements.Add(IndexLocalDeclaration(name));
            }

            statements.Add(statement);
        }

        return statements.Count == visited.Statements.Count
            ? visited
            : visited.WithStatements(List(statements));
    }

    private static LocalDeclarationStatementSyntax IndexLocalDeclaration(string name)
        => LocalDeclarationStatement(VariableDeclaration(
            PredefinedType(Token(SyntaxKind.IntKeyword)),
            SingletonSeparatedList(VariableDeclarator(name)
                .WithInitializer(EqualsValueClause(LiteralExpression(
                    SyntaxKind.NumericLiteralExpression, Literal(0)))))));

    // ---- *p / &x ----

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Kind() == SyntaxKind.PointerIndirectionExpression)
        {
            var operand = UnwrapParenthesized(node.Operand);

            if (TryBuildIndex(operand, out var arrayText, out var index))
            {
                var result = ElementAccessExpression(ParseExpression(arrayText))
                    .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                        Argument(Simplify(index)))));
                _statistics.PointerExprsIndexized++;
                return result;
            }

            if (IsLoweredExpression(operand))
                return node;

            if (IsOldPointerTyped(node.Operand))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"cannot statically prove what pointer expression '{node}' points to; the " +
                    "original file was preserved.",
                    node);
                return node;
            }
        }

        if (node.Kind() == SyntaxKind.AddressOfExpression)
        {
            var operand = UnwrapParenthesized(node.Operand);

            if (IsLoweredExpression(operand))
                return node;

            // R6 阶段仍游离的取址（fixed 初始式已被 R3 全部收编）= 逃逸。
            if (IsOldPointerProducing(node))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"address-of expression '{node}' escapes the supported forms (managed addresses " +
                    "may not be produced outside a fixed/array-index form); the original file was preserved.",
                    node);
                return node;
            }
        }

        if (node.Kind() is SyntaxKind.PreIncrementExpression
            or SyntaxKind.PreDecrementExpression)
        {
            return HandleStandaloneMutation(node, node.Operand);
        }

        return base.VisitPrefixUnaryExpression(node);
    }

    // ---- p++/p-- 独立使用 ----

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.Kind() is SyntaxKind.PostIncrementExpression
            or SyntaxKind.PostDecrementExpression)
        {
            return HandleStandaloneMutation(node, node.Operand);
        }

        return base.VisitPostfixUnaryExpression(node);
    }

    private SyntaxNode HandleStandaloneMutation(SyntaxNode node, ExpressionSyntax rawOperand)
    {
        var target = UnwrapParenthesized(rawOperand);
        if (IsLoweredExpression(target)) return node;

        var symbol = GetExpressionSymbol(target);
        if (symbol != null && _flowModel.TryGet(symbol, out var flow))
        {
            // 配对折叠游标（形参 int 索引/shadow int 局部/别名 int 局部）：
            // 自增直接作用于既有 int 变量，无需合成索引局部。
            if (flow is { IsProvable: true, FoldsToOffset: true })
            {
                _statistics.PointerIncrements++;
                return BuildMutationExpression(flow.OffsetText, node.Kind());
            }

            var plan = _plan!;
            if (!flow.IsProvable || plan.BlockedSites.Contains(node)
                || !plan.Locals.TryGetValue(symbol, out var local))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF022,
                    $"pointer increment/decrement '{node}' cannot fold back to an index local at " +
                    "this site (expression body/lambda capture/discarded arithmetic); the original " +
                    "file was preserved.",
                    node);
                return node;
            }

            _statistics.PointerIncrements++;
            return BuildMutationExpression(local.Name, node.Kind());
        }

        if (IsOldPointerTyped(target))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer increment/decrement '{node}' mutates a symbol whose source is not " +
                "provable; the original file was preserved.",
                node);
            return node;
        }

        return node is PrefixUnaryExpressionSyntax prefix
            ? base.VisitPrefixUnaryExpression(prefix)
            : base.VisitPostfixUnaryExpression((PostfixUnaryExpressionSyntax)node);
    }

    private static ExpressionSyntax BuildMutationExpression(string localName, SyntaxKind kind)
    {
        var identifier = IdentifierName(localName);
        return kind switch
        {
            SyntaxKind.PostIncrementExpression => PostfixUnaryExpression(
                SyntaxKind.PostIncrementExpression, identifier),
            SyntaxKind.PostDecrementExpression => PostfixUnaryExpression(
                SyntaxKind.PostDecrementExpression, identifier),
            SyntaxKind.PreIncrementExpression => PrefixUnaryExpression(
                SyntaxKind.PreIncrementExpression, identifier),
            SyntaxKind.PreDecrementExpression => PrefixUnaryExpression(
                SyntaxKind.PreDecrementExpression, identifier),
            _ => identifier,
        };
    }

    // ---- p[i] ----

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        var receiver = UnwrapParenthesized(node.Expression);

        if (GetExpressionSymbol(receiver) is { } symbol
            && _flowModel.TryGet(symbol, out var flow))
        {
            if (!flow.IsProvable)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer element access '{node}' uses a symbol whose source is not provable; " +
                    "the original file was preserved.",
                    node);
                return node;
            }

            if (node.ArgumentList.Arguments.Count != 1)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer element access '{node}' has an unsupported argument list.",
                    node);
                return node;
            }

            var arg = node.ArgumentList.Arguments[0].Expression;
            var offsetText = GetOffsetText(symbol, flow);

            ExpressionSyntax index;
            if (offsetText == "0")
            {
                index = arg;
            }
            else
            {
                // Simplify：pCur[0]→pCurIndex、pCur[1]→pCurIndex + 1，去掉
                // 恒等括号与右零，复杂实参保留括号（语义安全）。
                index = Simplify(BinaryExpression(
                    SyntaxKind.AddExpression,
                    ParenthesizedExpression(ParseExpression(offsetText)),
                    ParenthesizedExpression(arg)));
            }

            var result = ElementAccessExpression(ParseExpression(flow.ArrayExpressionText))
                .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                    Argument(index))));

            // 偏移 0 且数组名即接收者名时输出与原节点同构：无需计数，返回原节点。
            if (offsetText == "0"
                && flow.ArrayExpressionText == receiver.ToString())
            {
                return base.VisitElementAccessExpression(node);
            }

            _statistics.PointerExprsIndexized++;
            return result;
        }

        if (IsLoweredExpression(receiver))
            return base.VisitElementAccessExpression(node);

        return base.VisitElementAccessExpression(node);
    }

    // ---- p->m ----

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (node.Kind() == SyntaxKind.PointerMemberAccessExpression)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"struct pointer member access '{node}' (p->m) cannot be normalized: it requires a " +
                "provable array element of the pointed-at struct, which the current model does not " +
                "support; the original file was preserved.",
                node);
            return base.VisitMemberAccessExpression(node);
        }

        return base.VisitMemberAccessExpression(node);
    }

    // ---- (int*)p / (int)p ----

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var operand = UnwrapParenthesized(node.Expression);

        var targetIsPointer = node.Type is PointerTypeSyntax or FunctionPointerTypeSyntax;

        if (IsLoweredExpression(operand))
            return base.VisitCastExpression(node);

        if (targetIsPointer && IsOldIntegerTyped(operand))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF021,
                $"cast '{node}' converts an integer to a pointer; pointer origins cannot be proven " +
                "from an integer value, and the cast is never passed through unchanged.",
                node);
            return node;
        }

        // (T)(p - q)：probe 实测该二元自身类型是 long，不能用表达式整体
        // 类型判定；必须看两侧旧类型是否为指针。同源→int 索引差。
        if (operand is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.SubtractExpression } difference
            && OldType(difference.Left) is IPointerTypeSymbol
            && OldType(difference.Right) is IPointerTypeSymbol
            && TryBuildIndexDifference(difference, out _, out var diffIndex))
        {
            var lowered = Simplify(diffIndex);
            if (node.Type is PredefinedTypeSyntax
                { Keyword.RawKind: (int)SyntaxKind.IntKeyword })
            {
                return lowered;
            }

            return CastExpression(node.Type, ParenthesizedExpression(lowered));
        }

        if (IsOldPointerTyped(operand))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF021,
                $"cast '{node}' takes a pointer's absolute address (or changes its pointer kind); " +
                "only a same-array pointer difference wrapped in an integral cast is supported, and " +
                "the cast is never passed through unchanged.",
                node);
            return node;
        }

        return base.VisitCastExpression(node);
    }

    // ---- 算术 / 比较 ----

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (!IsSupportedPointerFamily(node.Kind()))
            return base.VisitBinaryExpression(node);

        if (IsLoweredExpression(node))
            return base.VisitBinaryExpression(node);

        var plan = _plan!;
        if (plan.BlockedSites.Contains(node))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF022,
                $"pointer arithmetic at '{node.ToString().Replace("\n", " ")}' cannot place its " +
                "index local at this site; the original file was preserved.",
                node);
            return node;
        }

        var leftType = OldType(node.Left);
        var rightType = OldType(node.Right);

        var leftPointer = leftType is IPointerTypeSymbol;
        var rightPointer = rightType is IPointerTypeSymbol;

        // p == null / p != null：数组空检查，正常托管比较。
        if (node.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
            && (node.Left.IsKind(SyntaxKind.NullLiteralExpression)
                || node.Right.IsKind(SyntaxKind.NullLiteralExpression)))
        {
            return base.VisitBinaryExpression(node);
        }

        // 两侧都不是指针（如 *p++ < 0xA0：解引用 byte 与 int 比较；
        // 或纯 int 运算）：普通值比较，子节点各自归一即可。
        if (!leftPointer && !rightPointer)
            return base.VisitBinaryExpression(node);

        // 指针 ± int（Add 两侧均可指针在右；Sub 指针须在左）。
        if (node.Kind() == SyntaxKind.AddExpression
            && ((leftPointer && !rightPointer) || (!leftPointer && rightPointer))
            && TryBuildIndex(node, out _, out var index))
        {
            return FinishPropagation(node, Simplify(index));
        }

        if (node.Kind() == SyntaxKind.SubtractExpression
            && leftPointer && !rightPointer
            && TryBuildIndex(node, out _, out var subIndex))
        {
            return FinishPropagation(node, Simplify(subIndex));
        }

        // p - q：两指针相减。
        if (node.Kind() == SyntaxKind.SubtractExpression && leftPointer && rightPointer)
        {
            if (TryBuildIndexDifference(node, out var reason, out var diffIndex))
            {
                return FinishPropagation(node, Simplify(diffIndex));
            }

            AddError(
                reason == DifferenceReason.Unprovable
                    ? UnsafeFfmDiagnosticCode.UF020
                    : UnsafeFfmDiagnosticCode.UF020,
                $"pointer subtraction '{node.ToString().Replace("\n", " ")}' is not between " +
                (reason == DifferenceReason.DifferentSource
                    ? "pointers of the same provable source"
                    : "pointers with a provable source")
                + "; the original file was preserved.",
                node);
            return node;
        }

        // 指针比较。
        if (IsComparison(node.Kind()))
        {
            if (leftPointer && rightPointer
                && TryBuildComparison(node, out var comparison))
            {
                return FinishPropagation(node, Simplify(comparison));
            }

            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer comparison '{node.ToString().Replace("\n", " ")}' is not between " +
                "pointers of the same provable source; the original file was preserved.",
                node);
            return node;
        }

        // 结构上像指针族但无法证明（操作数为未知指针表达式等）。
        if (leftPointer || rightPointer)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer expression '{node.ToString().Replace("\n", " ")}' has an unprovable " +
                "source; the original file was preserved.",
                node);
            return node;
        }

        return base.VisitBinaryExpression(node);
    }

    private SyntaxNode FinishPropagation(ExpressionSyntax node, ExpressionSyntax lowered)
    {
        // 表达式语句形态的算术/差值/比较：值被丢弃，归一后无法作为合法语句
        // （CS0201），属不可回填结果 → UF022。
        if (node.Parent is ExpressionStatementSyntax)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF022,
                $"pointer arithmetic expression '{node.ToString().Replace("\n", " ")}' is used as a " +
                "statement and its offset result cannot be folded back to a use site; the original " +
                "file was preserved.",
                node);
            return node;
        }

        return lowered;
    }

    private static bool IsSupportedPointerFamily(SyntaxKind kind) =>
        kind is SyntaxKind.AddExpression
            or SyntaxKind.SubtractExpression
            or SyntaxKind.LessThanExpression
            or SyntaxKind.GreaterThanExpression
            or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanOrEqualExpression
            or SyntaxKind.EqualsExpression
            or SyntaxKind.NotEqualsExpression;

    private static bool IsComparison(SyntaxKind kind) =>
        kind is SyntaxKind.LessThanExpression
            or SyntaxKind.GreaterThanExpression
            or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanOrEqualExpression
            or SyntaxKind.EqualsExpression
            or SyntaxKind.NotEqualsExpression;

    // ==================================================================
    // 索引表达式构造：把指针表达式递归翻译为「数组文本 + int 索引表达式」。
    // 遍历中计数：算术传播、自增（每个节点仅被消费一次，见类注释）。
    // ==================================================================

    private bool TryBuildIndex(
        ExpressionSyntax raw,
        out string arrayText,
        out ExpressionSyntax index)
        => TryBuildIndex(raw, out arrayText, out index, out _);

    private bool TryBuildIndex(
        ExpressionSyntax raw,
        out string arrayText,
        out ExpressionSyntax index,
        out bool pairedDomain)
    {
        var e = UnwrapParenthesized(raw);
        arrayText = string.Empty;
        index = default!;
        pairedDomain = false;

        switch (e)
        {
            case IdentifierNameSyntax:
                if (GetExpressionSymbol(e) is { } identifierSymbol)
                {
                    return TryResolveIndexSymbol(
                        identifierSymbol, e.ToString(),
                        out arrayText, out index, out pairedDomain);
                }

                return false;

            case MemberAccessExpressionSyntax memberAccess:
                // holder.Value 读（配对 holder 形参或已重建 holder 局部）：
                // 继承 holder 当前挂载的数组，索引即 .Value 承载的 int。
                if (memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                    && memberAccess.Name.Identifier.ValueText == "Value")
                {
                    var holderSymbol = GetExpressionSymbol(memberAccess.Expression);
                    string? holderArray = null;
                    if (holderSymbol != null)
                    {
                        _holderParameters.TryGetValue(holderSymbol, out holderArray);
                        if (holderArray == null)
                            _holderLocals.TryGetValue(holderSymbol, out holderArray);
                    }

                    if (holderArray != null)
                    {
                        arrayText = holderArray;
                        index = MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            memberAccess.Expression,
                            IdentifierName("Value"));
                        pairedDomain = true;
                        return true;
                    }
                }

                if (GetExpressionSymbol(e) is { } memberSymbol)
                {
                    return TryResolveIndexSymbol(
                        memberSymbol, e.ToString(),
                        out arrayText, out index, out pairedDomain);
                }

                return false;

            case BinaryExpressionSyntax binary when IsSupportedPointerFamily(binary.Kind()):
                var bLeftType = OldType(binary.Left);
                var bRightType = OldType(binary.Right);
                var bLeftPointer = bLeftType is IPointerTypeSymbol;
                var bRightPointer = bRightType is IPointerTypeSymbol;

                if (binary.Kind() == SyntaxKind.AddExpression
                    && ((bLeftPointer && !bRightPointer)
                        || (!bLeftPointer && bRightPointer)))
                {
                    ExpressionSyntax pointerSide;
                    ExpressionSyntax intSide;
                    if (bLeftPointer)
                    {
                        pointerSide = binary.Left;
                        intSide = binary.Right;
                    }
                    else
                    {
                        pointerSide = binary.Right;
                        intSide = binary.Left;
                    }

                    if (!TryBuildIndex(pointerSide, out var nestedArray, out var nestedIndex,
                            out var nestedPaired))
                    {
                        return false;
                    }

                    var visitedIntSide = (ExpressionSyntax)Visit(intSide)!;
                    var combined = Simplify(BinaryExpression(
                        SyntaxKind.AddExpression,
                        ParenthesizedExpression(nestedIndex),
                        ParenthesizedExpression(visitedIntSide)));

                    arrayText = nestedArray;
                    index = combined;
                    pairedDomain = nestedPaired;
                    _statistics.PointerArithmeticPropagated++;
                    return true;
                }

                if (binary.Kind() == SyntaxKind.SubtractExpression
                    && bLeftPointer && !bRightPointer)
                {
                    if (!TryBuildIndex(binary.Left, out var nestedArray, out var nestedIndex,
                            out var nestedPaired))
                    {
                        return false;
                    }

                    var visitedIntSide = (ExpressionSyntax)Visit(binary.Right)!;
                    var combined = Simplify(BinaryExpression(
                        SyntaxKind.SubtractExpression,
                        ParenthesizedExpression(nestedIndex),
                        ParenthesizedExpression(visitedIntSide)));

                    arrayText = nestedArray;
                    index = combined;
                    pairedDomain = nestedPaired;
                    _statistics.PointerArithmeticPropagated++;
                    return true;
                }

                // 指针差值/比较不是「单个数组+索引」形态，由调用方专门处理。
                return false;

            case ConditionalExpressionSyntax conditional:
                if (TryBuildIndex(conditional.WhenTrue, out var trueArray, out var trueIndex,
                        out var truePaired)
                    && TryBuildIndex(conditional.WhenFalse, out var falseArray, out var falseIndex,
                        out var falsePaired)
                    && trueArray == falseArray)
                {
                    arrayText = trueArray;
                    index = ConditionalExpression(
                        conditional.Condition,
                        ParenthesizedExpression(trueIndex),
                        ParenthesizedExpression(falseIndex));
                    pairedDomain = truePaired || falsePaired;
                    return true;
                }

                return false;

            case CheckedExpressionSyntax checkedExpression:
                return TryBuildIndex(checkedExpression.Expression,
                    out arrayText, out index, out pairedDomain);

            case PostfixUnaryExpressionSyntax postfix
                when postfix.Kind() is SyntaxKind.PostIncrementExpression
                    or SyntaxKind.PostDecrementExpression:
                return TryBuildMutationIndex(postfix.Operand, postfix,
                    out arrayText, out index, out pairedDomain);

            case PrefixUnaryExpressionSyntax prefix
                when prefix.Kind() is SyntaxKind.PreIncrementExpression
                    or SyntaxKind.PreDecrementExpression:
                return TryBuildMutationIndex(prefix.Operand, prefix,
                    out arrayText, out index, out pairedDomain);

            case InvocationExpressionSyntax invocation
                when TryGetInvocationPlan(invocation) is { } calleePlan:
                var rebuilt = ExpandPairedInvocation(invocation, calleePlan, out var firstArray);
                if (firstArray == null) return false;
                arrayText = firstArray;
                index = rebuilt;
                pairedDomain = true;
                return true;

            case CastExpressionSyntax cast
                when cast.Expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.SubtractExpression } innerDifference
                    && IsOldPointerTyped(innerDifference.Left):
                // 嵌套 (T)(p-q)：内层差值构造。
                return TryBuildIndex(cast.Expression, out arrayText, out index, out pairedDomain);

            default:
                return false;
        }
    }

    private bool TryBuildMutationIndex(
        ExpressionSyntax rawTarget,
        SyntaxNode mutation,
        out string arrayText,
        out ExpressionSyntax index,
        out bool pairedDomain)
    {
        var target = UnwrapParenthesized(rawTarget);
        var symbol = GetExpressionSymbol(target);

        // 配对折叠游标：自增直接作用在配对索引变量自身。
        if (IsFoldsSymbol(symbol))
        {
            var foldsFlow = _flowModel.TryGet(symbol!, out var f) ? f : default;
            arrayText = foldsFlow.ArrayExpressionText;
            index = BuildMutationExpression(foldsFlow.OffsetText, mutation.Kind());
            pairedDomain = true;
            _statistics.PointerIncrements++;
            return true;
        }

        // R3 降级游标：标识符本身即 int 索引局部，原地自增。
        if (symbol != null && _loweredArrayMap.TryGetValue(symbol, out var lowered))
        {
            arrayText = lowered.ArrayName;
            index = BuildMutationExpression(target.ToString(), mutation.Kind());
            pairedDomain = false;
            _statistics.PointerIncrements++;
            return true;
        }

        if (symbol != null && _flowModel.TryGet(symbol, out var flow) && flow.IsProvable)
        {
            var plan = _plan!;
            if (plan.BlockedSites.Contains(mutation)
                || !plan.Locals.TryGetValue(symbol, out var local))
            {
                arrayText = default!;
                index = default!;
                pairedDomain = false;
                return false;
            }

            arrayText = flow.ArrayExpressionText;
            index = BuildMutationExpression(local.Name, mutation.Kind());
            pairedDomain = false;
            _statistics.PointerIncrements++;
            return true;
        }

        arrayText = default!;
        index = default!;
        pairedDomain = false;
        return false;
    }

    private enum DifferenceReason { SameSource, DifferentSource, Unprovable }

    private bool TryBuildIndexDifference(
        BinaryExpressionSyntax node,
        out DifferenceReason reason,
        out ExpressionSyntax diffIndex)
    {
        diffIndex = default!;

        var leftOk = TryBuildIndex(node.Left, out var leftArray, out var leftIndex,
            out var leftPaired);
        var rightOk = TryBuildIndex(node.Right, out var rightArray, out var rightIndex,
            out var rightPaired);

        if (!leftOk || !rightOk)
        {
            reason = DifferenceReason.Unprovable;
            return false;
        }

        // 配对方法入口契约：形参索引（含 shadow/别名）相对同一入参数组空间，
        // 形参索引间、形参索引与本方法 fixed 索引间允许相减；至少一侧为
        // 配对折叠索引时放宽同源要求。
        if (leftArray != rightArray && !(leftPaired || rightPaired))
        {
            reason = DifferenceReason.DifferentSource;
            return false;
        }

        diffIndex = BinaryExpression(
            SyntaxKind.SubtractExpression,
            ParenthesizedExpression(leftIndex),
            ParenthesizedExpression(rightIndex));
        _statistics.PointerDifferences++;
        reason = DifferenceReason.SameSource;
        return true;
    }

    private bool TryBuildComparison(
        BinaryExpressionSyntax node,
        out ExpressionSyntax comparison)
    {
        comparison = default!;

        if (!TryBuildIndex(node.Left, out var leftArray, out var leftIndex, out var leftPaired)
            || !TryBuildIndex(node.Right, out var rightArray, out var rightIndex, out var rightPaired))
        {
            return false;
        }

        if (leftArray != rightArray && !(leftPaired || rightPaired))
            return false;

        comparison = BinaryExpression(
            node.Kind(),
            ParenthesizedExpression(leftIndex),
            ParenthesizedExpression(rightIndex));
        _statistics.PointerComparisons++;
        return true;
    }

    // ---- 简单常量折叠：消除 0 偏移噪音 ----

    private static ExpressionSyntax Simplify(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case BinaryExpressionSyntax binary:
                var left = Simplify(binary.Left);
                var right = Simplify(binary.Right);
                var bareLeft = UnwrapParens(left);
                var bareRight = UnwrapParens(right);

                if (IsZeroLiteral(bareLeft))
                {
                    if (binary.IsKind(SyntaxKind.AddExpression))
                        return bareRight;
                    if (binary.IsKind(SyntaxKind.SubtractExpression))
                        return Simplify(PrefixUnaryExpression(
                            SyntaxKind.UnaryMinusExpression, bareRight));
                }

                // 右零消去仅对加/减成立（比较 0 == x 不能折叠为 0）。
                if (IsZeroLiteral(bareRight)
                    && binary.Kind() is SyntaxKind.AddExpression
                        or SyntaxKind.SubtractExpression)
                {
                    return bareLeft;
                }

                // 两个数值字面量（仅加减运算）：int 域直接折叠；溢出放弃。
                var foldableArithmetic = binary.IsKind(SyntaxKind.AddExpression)
                    || binary.IsKind(SyntaxKind.SubtractExpression);
                if (foldableArithmetic
                    && TryGetIntLiteral(bareLeft, out var leftLiteral)
                    && TryGetIntLiteral(bareRight, out var rightLiteral))
                {
                    int folded;
                    try
                    {
                        folded = binary.IsKind(SyntaxKind.AddExpression)
                            ? checked(leftLiteral + rightLiteral)
                            : checked(leftLiteral - rightLiteral);
                    }
                    catch (System.OverflowException)
                    {
                        goto keepBinary;
                    }

                    return LiteralExpression(
                        SyntaxKind.NumericLiteralExpression, Literal(folded));
                }

                keepBinary:
                return binary.WithLeft(left).WithRight(right);

            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } unary:
                var operand = Simplify(unary.Operand);
                return IsZeroLiteral(UnwrapParens(operand))
                    ? LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))
                    : unary.WithOperand(operand);

            case ParenthesizedExpressionSyntax parenthesized:
                var inner = Simplify(parenthesized.Expression);
                return inner is BinaryExpressionSyntax or PrefixUnaryExpressionSyntax
                    ? ParenthesizedExpression(inner)
                    : inner;

            default:
                return expression;
        }
    }

    private static ExpressionSyntax UnwrapParens(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && literal.Token.ValueText == "0";

    private static bool TryGetIntLiteral(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is LiteralExpressionSyntax
            { RawKind: (int)SyntaxKind.NumericLiteralExpression } numeric
            && numeric.Token.Value is int parsed)
        {
            value = parsed;
            return true;
        }

        return false;
    }

    // ---- 赋值（含 +=/-= 复合） ----

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // 指针复合赋值：p += n / p -= n → 索引局部复合赋值。
        if (node.Kind() is SyntaxKind.AddAssignmentExpression
            or SyntaxKind.SubtractAssignmentExpression)
        {
            var left = UnwrapParenthesized(node.Left);
            if (!IsLoweredExpression(left))
            {
                var symbol = GetExpressionSymbol(left);
                if (symbol != null && _flowModel.TryGet(symbol, out var flow) && flow.IsProvable)
                {
                    // 配对折叠游标：复合赋值直接作用于既有 int 变量。
                    if (flow.FoldsToOffset)
                    {
                        var visitedFoldsRight = (ExpressionSyntax)Visit(node.Right)!;
                        _statistics.PointerArithmeticPropagated++;
                        return AssignmentExpression(
                            node.Kind(),
                            ParseExpression(flow.OffsetText),
                            visitedFoldsRight);
                    }

                    var plan = _plan!;
                    if (plan.BlockedSites.Contains(node)
                        || !plan.Locals.TryGetValue(symbol, out var local))
                    {
                        AddError(
                            UnsafeFfmDiagnosticCode.UF022,
                            $"compound pointer assignment '{node.ToString().Replace("\n", " ")}' " +
                            "cannot place its index local; the original file was preserved.",
                            node);
                        return node;
                    }

                    _statistics.PointerArithmeticPropagated++;
                    return AssignmentExpression(
                        node.Kind(),
                        IdentifierName(local.Name),
                        node.Right);
                }

                if (IsOldPointerTyped(left))
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF020,
                        $"compound pointer assignment '{node.ToString().Replace("\n", " ")}' has an " +
                        "unprovable source; the original file was preserved.",
                        node);
                    return node;
                }
            }
        }

        if (node.Kind() == SyntaxKind.SimpleAssignmentExpression)
        {
            var rhs = UnwrapParenthesized(node.Right);

            // RHS 为配对指针返回方法调用：实参展开、结果按配对约定是同源
            // int 索引（p = Entity(p) 形态），整体重建为 int 赋值。
            if (rhs is InvocationExpressionSyntax rhsInvocation
                && TryGetInvocationPlan(rhsInvocation) is { } rhsPlan)
            {
                var rebuiltRight = ExpandPairedInvocation(rhsInvocation, rhsPlan, out _);
                var visitedInvocationLeft = (ExpressionSyntax)Visit(node.Left)!;
                return node.Update(
                    visitedInvocationLeft, node.OperatorToken, rebuiltRight);
            }

            // 配对方法域内其它可证指针 RHS（形参索引±n/shadow 派生）：归一
            // 为 int 索引表达式赋给同名 int 游标；不可证形态仍落既有 UF020。
            if (_pairedStack.Count > 0
                && !IsLoweredExpression(rhs)
                && IsOldPointerTyped(rhs)
                && TryBuildIndex(rhs, out _, out var pairedRhs, out _))
            {
                var visitedPairedLeft = (ExpressionSyntax)Visit(node.Left)!;
                return node.Update(
                    visitedPairedLeft, node.OperatorToken, Simplify(pairedRhs));
            }
        }

        var visitedRight = (ExpressionSyntax)Visit(node.Right)!;

        var rightIsFlow = TryGetFlow(node.Right, out var rightFlow);
        var rightIsOldPointer = IsOldPointerTyped(node.Right);

        if (!IsLoweredExpression(node.Right) && (rightIsOldPointer || rightIsFlow))
        {
            if (rightIsFlow && rightFlow.IsProvable)
            {
                // 数组引用赋值（含 q = p）：正常托管赋值，保留。
                var visitedLeft = (ExpressionSyntax)Visit(node.Left)!;
                return node.Update(visitedLeft, node.OperatorToken, visitedRight);
            }

            if (node.Right.IsKind(SyntaxKind.NullLiteralExpression))
            {
                var visitedLeft = (ExpressionSyntax)Visit(node.Left)!;
                return node.Update(visitedLeft, node.OperatorToken, visitedRight);
            }

            var code = node.Right is BinaryExpressionSyntax
                ? UnsafeFfmDiagnosticCode.UF022
                : UnsafeFfmDiagnosticCode.UF020;
            AddError(
                code,
                $"assignment '{node.ToString().Replace("\n", " ")}' assigns a pointer value whose " +
                "source is not provable in the current model; the original file was preserved.",
                node);
            return node;
        }

        var visitedLeftFinal = (ExpressionSyntax)Visit(node.Left)!;
        if (visitedLeftFinal == node.Left && visitedRight == node.Right) return node;
        return node.Update(visitedLeftFinal, node.OperatorToken, visitedRight);
    }

    public override SyntaxNode? VisitArgument(ArgumentSyntax node)
    {
        var expression = UnwrapParenthesized(node.Expression);

        // 配对方法调用与 holder.Create 的实参由 VisitInvocationExpression
        // 统一重建承载（指针实参展开为数组+索引、访问器 lambda 改 int 捕获），
        // 此处不前置逃逸判决。
        if (node.Parent is ArgumentListSyntax { Parent: InvocationExpressionSyntax parentInvocation }
            && (TryGetInvocationPlan(parentInvocation) != null
                || IsHolderCreateInvocation(parentInvocation)))
        {
            return base.VisitArgument(node);
        }

        if (IsLoweredExpression(expression))
            return base.VisitArgument(node);

        if (TryGetFlow(expression, out var flow))
        {
            if (!flow.IsProvable)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer expression '{node.Expression}' with an unprovable source is passed as " +
                    "an argument; the original file was preserved.",
                    node);
                return node;
            }

            // 可证数组实参：正常托管调用，保留。
            return base.VisitArgument(node);
        }

        if (IsOldPointerTyped(expression))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer expression '{node.Expression}' is passed as an argument and escapes the " +
                "provable domain; the original file was preserved.",
                node);
            return node;
        }

        return base.VisitArgument(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression != null)
        {
            var expression = UnwrapParenthesized(node.Expression);

            if (IsLoweredExpression(expression))
                return base.VisitReturnStatement(node);

            // 配对指针返回方法：返回表达式（形参/shadow/别名索引、指针±n、
            // 配对指针返回调用、fixed 降级索引）按入口契约归一为 int 索引；
            // R5 已把本方法返回类型改为 int。不可证形态保留 UF020。
            if (_pairedStack.Count > 0
                && _pairedStack[^1].ReturnsPointer
                && !expression.IsKind(SyntaxKind.NullLiteralExpression)
                && IsOldPointerTyped(expression))
            {
                if (TryBuildIndex(expression, out _, out var returnIndex, out _))
                {
                    _statistics.PointerExprsIndexized++;
                    var rebuilt = Simplify(returnIndex);
                    return node.Update(node.ReturnKeyword, rebuilt, node.SemicolonToken);
                }

                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer expression '{node.Expression}' is returned from a paired method " +
                    "but its index origin is not provable; the original file was preserved.",
                    node);
                return node;
            }

            if (TryGetFlow(expression, out var flow))
            {
                if (!flow.IsProvable)
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF020,
                        $"pointer expression '{node.Expression}' with an unprovable source is " +
                        "returned; the original file was preserved.",
                        node);
                    return node;
                }

                // 可证数组返回：保留。
                return base.VisitReturnStatement(node);
            }

            if (IsOldPointerTyped(expression) && !expression.IsKind(SyntaxKind.NullLiteralExpression))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer expression '{node.Expression}' is returned and escapes the provable " +
                    "domain; the original file was preserved.",
                    node);
                return node;
            }
        }

        return base.VisitReturnStatement(node);
    }

    // ---- flow / lowered / 旧类型判定 ----

    private string GetOffsetText(ISymbol symbol, PointerFlow flow)
    {
        if (_plan != null && _plan.Locals.TryGetValue(symbol, out var local))
            return local.Name;

        return flow.OffsetText;
    }

    private bool TryGetFlow(ExpressionSyntax expression, out PointerFlow flow)
    {
        if (GetExpressionSymbol(expression) is { } symbol
            && _flowModel.TryGet(symbol, out var found))
        {
            flow = found;
            return true;
        }

        flow = default!;
        return false;
    }

    /// <summary>
    /// 表达式是否为 R3 降级域（旧指针符号现已全部是 int 索引）。递归覆盖
    /// 标识符、加减/比较二元、括号、条件与一元包装（如 lowered fixed 块内的 p+1）。
    /// 非指针来源符号（整型局部、message.Length 等）只是 int 运算的普通
    /// 操作数，不要求进入 lowered 集合；判定只针对「旧指针来源」符号：
    /// 它们必须全部由 R3 降级——R5 参数 flow 符号（旧类型同样是指针但不在
    /// lowered 集合）与未知指针不允许，其算术/比较仍属 M3b 拒绝域。
    /// </summary>
    private bool IsLoweredExpression(ExpressionSyntax expression)
    {
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        CollectOriginatingSymbols(expression, symbols);
        if (symbols.Count == 0) return false;

        var hasPointerSource = false;
        foreach (var symbol in symbols)
        {
            if (!IsOldPointerSymbol(symbol)) continue;
            hasPointerSource = true;
            if (!_loweredSymbols.Contains(symbol)) return false;
        }

        return hasPointerSource;
    }

    /// <summary>符号的旧声明类型是否为指针（local/parameter/field）。</summary>
    private static bool IsOldPointerSymbol(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type is IPointerTypeSymbol,
        IParameterSymbol parameter => parameter.Type is IPointerTypeSymbol,
        IFieldSymbol field => field.Type is IPointerTypeSymbol,
        _ => false,
    };

    private void CollectOriginatingSymbols(
        ExpressionSyntax expression, HashSet<ISymbol> symbols)
    {
        expression = UnwrapParenthesized(expression);

        switch (expression)
        {
            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                if (GetExpressionSymbol(expression) is { } direct) symbols.Add(direct);
                return;

            case BinaryExpressionSyntax binary:
                CollectOriginatingSymbols(binary.Left, symbols);
                CollectOriginatingSymbols(binary.Right, symbols);
                return;

            case ConditionalExpressionSyntax conditional:
                CollectOriginatingSymbols(conditional.WhenTrue, symbols);
                CollectOriginatingSymbols(conditional.WhenFalse, symbols);
                return;

            case PrefixUnaryExpressionSyntax prefix:
                CollectOriginatingSymbols(prefix.Operand, symbols);
                return;

            case PostfixUnaryExpressionSyntax postfix:
                CollectOriginatingSymbols(postfix.Operand, symbols);
                return;

            case CastExpressionSyntax cast:
                CollectOriginatingSymbols(cast.Expression, symbols);
                return;

            case CheckedExpressionSyntax checkedExpression:
                CollectOriginatingSymbols(checkedExpression.Expression, symbols);
                return;
        }
    }

    private bool IsOldPointerTyped(ExpressionSyntax expression)
    {
        expression = UnwrapParenthesized(expression);

        if (expression is IdentifierNameSyntax or MemberAccessExpressionSyntax)
            return GetExpressionSymbol(expression) switch
            {
                ILocalSymbol local => local.Type is IPointerTypeSymbol,
                IParameterSymbol parameter => parameter.Type is IPointerTypeSymbol,
                IFieldSymbol field => field.Type is IPointerTypeSymbol,
                _ => false,
            };

        return GetOldTypeInfo(expression).Type is IPointerTypeSymbol;
    }

    private bool IsOldPointerProducing(ExpressionSyntax expression)
    {
        if (IsOldPointerTyped(expression)) return true;
        var operand = expression switch
        {
            PrefixUnaryExpressionSyntax prefix => prefix.Operand,
            _ => null,
        };
        return operand != null && IsOldPointerTyped(UnwrapParenthesized(operand));
    }

    private bool IsOldIntegerTyped(ExpressionSyntax expression)
    {
        var type = GetOldTypeInfo(expression).Type;
        if (type == null) return false;
        if (type.SpecialType is SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
            or SpecialType.System_Byte or SpecialType.System_SByte)
        {
            return true;
        }

        return type.TypeKind == TypeKind.Enum;
    }

    private ITypeSymbol? OldType(ExpressionSyntax expression)
        => GetOldTypeInfo(expression).Type;

    private TypeInfo GetOldTypeInfo(ExpressionSyntax expression)
        => _resolver.Resolve(expression) is { } original
            ? _model.GetTypeInfo(original)
            : default;

    private ISymbol? GetExpressionSymbol(ExpressionSyntax expression)
        => _resolver.Resolve(expression) is { } original
            ? _model.GetSymbolInfo(original).Symbol
            : null;

    private static ExpressionSyntax UnwrapParenthesized(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private void AddError(
        UnsafeFfmDiagnosticCode code, string message, SyntaxNode node)
    {
        var line = 1;
        var character = 1;
        var positionNode = _resolver.Resolve(node) ?? node;
        try
        {
            var span = positionNode.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            character = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // 脱离语法树的节点无法计算行号时退回 (1,1)。
        }

        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error, code, message, null, line, character));
        _statistics.Rejections++;
        switch (code)
        {
            case UnsafeFfmDiagnosticCode.UF020: _statistics.Uf020Rejections++; break;
            case UnsafeFfmDiagnosticCode.UF021: _statistics.Uf021Rejections++; break;
            case UnsafeFfmDiagnosticCode.UF022: _statistics.Uf022Rejections++; break;
        }
    }
}
