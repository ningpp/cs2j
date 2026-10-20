using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R3：fixed 语句改写（设计 D2/D2b/D2c，§4 组件表）。整个 fixed 块改写为
/// 可编译的纯托管普通块。表示约定（语法实测：FixedStatementSyntax.Declaration
/// 的 Type 是 PointerTypeSyntax，每个 VariableDeclarator 带 Initializer）：
/// 每个固定指针 p → 数组引用局部 + int 索引局部，索引局部沿用原名 p，
/// 故体内算术/比较/自增文本天然合法；仅解引用、下标、指针声明等被重写。
/// 五类初始式：数组、&amp;arr[k]、指针别名、null、string，外加 &amp;scalar 单槽。
/// </summary>
internal sealed class FixedStatementRewriter : CSharpSyntaxRewriter
{
    public static readonly SyntaxAnnotation FixedReplacedAnnotation = new("c06-fixed-replaced");

    /// <summary>
    /// R3 合成下标注解：<c>*(cursor ± k)</c> / <c>cursor[k]</c> 归一出的
    /// <c>array[绝对下标]</c>。索引已是绝对托管下标，R6 不得再按接收者游标的
    /// 折叠偏移二次叠加基偏移（Uri.UnescapeOnly 的 <c>*(pnew - 1)</c> 场景）。
    /// </summary>
    public static readonly SyntaxAnnotation FixedIndexedAnnotation = new("c06-fixed-indexed");

    private enum InitKind { Array, Offset, Alias, Null, String, Scalar, Error }

    private sealed record OffsetPayload(ExpressionSyntax Receiver, ExpressionSyntax Index);
    private sealed record AliasPayload(string SourceArray, ExpressionSyntax Index, bool ReadOnly);

    private sealed class PtrInfo
    {
        public required string ArrayName { get; set; }

        /// <summary>
        /// 真源源槽键：指针比较/相减的同源判定按此键而非伴生数组名——同一真源
        /// 数组的多个 fixed 变量各有独立伴生名（_p_array/_q_array）但共享源槽。
        /// </summary>
        public required string SourceSlot { get; set; }

        public bool ReadOnlyString { get; init; }
        public bool Resolved { get; set; } = true;

        /// <summary>
        /// null 初值游标的伴生数组名（仅 Null 类帧/局部登记，永不随重绑定改写）。
        /// 该数组以 <c>null</c> 作为「指针为空」的托管表示：指针赋值时同步改写为
        /// 新数组（见 <see cref="VisitAssignmentExpression"/>），故
        /// <c>p == null</c> 归一为 <c>_p_array == null</c>（Uri.UnescapeOnly 的
        /// <c>pnew</c> 场景）。非 null 起源游标为 null，不参与该归一。
        /// </summary>
        public string? NullCompanionArray { get; set; }

        /// <summary>
        /// 是否为 byte*→char* 的 UTF-16 重解释局部（仅服务 new String 实参，
        /// 归一为 Encoding.Unicode.GetString）；为 true 时不承载 int 索引。
        /// </summary>
        public bool ReinterpretUnicode { get; init; }

        /// <summary>重解释局部来源的字节起始索引文本（相对 <see cref="ArrayName"/>）。</summary>
        public string? ByteIndexText { get; init; }
    }

    private sealed record FrameRecord(
        VariableDeclaratorSyntax Declarator,
        ILocalSymbol? Symbol,
        InitKind Kind,
        string ArrayName,
        object? Payload);

    private readonly SemanticModel _model;
    private readonly HashSet<IFieldSymbol> _rewrittenFields;
    private readonly HashSet<ISymbol> _pointerRewritten;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;
    private readonly PointerMethodCatalog _catalog;
    private readonly PointerFlowModel _flowModel;

    /// <summary>
    /// UF040 降级域：prescan 判定可降级的裸指针 stackalloc 局部（原始符号）。
    /// 命中时声明改写为「同块前置伴生堆数组 <c>T[] _p_array = new T[n];</c>
    /// + 同名 <c>int p = 0;</c>」，登记进 <see cref="_infos"/> 后其全部使用
    /// 由 R6 既有索引化/配对展开通道承载。
    /// </summary>
    private readonly HashSet<ISymbol> _lowerableStackAllocLocals;

    private readonly Dictionary<ISymbol, PtrInfo> _infos = new();
    private readonly HashSet<string> _generatedNames = new();
    private readonly Dictionary<SyntaxNode, HashSet<string>> _scopeNameCache = new();

    /// <summary>
    /// fixed 块外指针局部（资源流指针）的伴生语句注入：外层块 → 锚点语句
    /// （原指针局部声明）→ 需插在其前的语句。伴生数组必须与 int 索引局部同
    /// 处一块作用域，不能用嵌套块包裹（后续语句仍要引用两个名字）。
    /// </summary>
    private readonly Dictionary<BlockSyntax, Dictionary<StatementSyntax, List<StatementSyntax>>>
        _blockInjections = new();

    /// <summary>当前访问子树所属的配对方法计划栈（返回/赋值逃逸门放行用）。</summary>
    private readonly List<PairedMethodPlan> _pairedStack = new();

    public FixedStatementRewriter(
        SemanticModel model,
        HashSet<IFieldSymbol> rewrittenFields,
        HashSet<ISymbol> pointerRewritten,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver,
        PointerMethodCatalog catalog,
        PointerFlowModel flowModel,
        HashSet<ISymbol>? lowerableStackAllocLocals = null)
    {
        _model = model;
        _rewrittenFields = rewrittenFields;
        _pointerRewritten = pointerRewritten;
        _statistics = statistics;
        _resolver = resolver;
        _catalog = catalog;
        _flowModel = flowModel;
        _lowerableStackAllocLocals = lowerableStackAllocLocals
            ?? new HashSet<ISymbol>(SymbolEqualityComparer.Default);
    }

    /// <summary>
    /// R3 已降级指针符号 → 其数组名/只读标记（解析成功的 fixed/别名局部）。
    /// 供 R6 在跨域表达式（配对形参与 fixed 索引混算）中把这些 int 索引
    /// 重新还原为「数组 + 索引」对。
    /// </summary>
    public IReadOnlyDictionary<ISymbol, (string ArrayName, bool ReadOnly, string SourceSlot)>
        LoweredArrayMap
    {
        get
        {
            var map = new Dictionary<ISymbol, (string, bool, string)>(
                SymbolEqualityComparer.Default);
            foreach (var (symbol, info) in _infos)
            {
                if (info.Resolved && info.ArrayName.Length > 0)
                    map[symbol] = (info.ArrayName, info.ReadOnlyString, info.SourceSlot);
            }

            return map;
        }
    }

    public List<UnsafeFfmDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    /// <summary>
    /// 被 R3 降级为「数组局部 + int 索引」的原始指针符号集合（fixed 变量与
    /// 被转换的指针局部）。R6 据此把这些符号的体内使用识别为 int，不误判为指针。
    /// </summary>
    public IReadOnlyCollection<ISymbol> LoweredSymbols => _infos.Keys;

    // ---- 配对方法域：返回/赋值逃逸门按配对约定放行 ----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var plan = GetCurrentPlan(node);
        if (plan == null) return base.VisitMethodDeclaration(node);

        _pairedStack.Add(plan);
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

        _pairedStack.Add(plan);
        try
        {
            return base.VisitLocalFunctionStatement(node);
        }
        finally
        {
            _pairedStack.RemoveAt(_pairedStack.Count - 1);
        }
    }

    /// <summary>
    /// 节点当前所处的配对方法计划（R3 按方法/局部函数压栈）；重解释存储读
    /// 形参判定只在配对方法体内成立。
    /// </summary>
    private PairedMethodPlan? ReinterpretPlanForNode(SyntaxNode node)
        => _pairedStack.Count == 0 ? null : _pairedStack[^1];

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

    private bool IsPairedPointerReturnInvocation(ExpressionSyntax expression)
    {
        // 不限定包含方法是否配对：调用点可在非配对方法的 fixed 块内
        //（q = Entity(q) 形态），可证性来自 LHS 已是降级 int 索引局部。
        if (expression is not InvocationExpressionSyntax invocation) return false;

        var original = _resolver.Resolve(invocation);
        if (original is not InvocationExpressionSyntax originalInvocation) return false;

        var info = _model.GetSymbolInfo(originalInvocation);
        foreach (var candidate in EnumerateSymbols(info))
        {
            if (_catalog.GetPlan(candidate) is { ReturnsPointer: true })
                return true;
        }

        return false;
    }

    private static IEnumerable<IMethodSymbol> EnumerateSymbols(SymbolInfo info)
    {
        if (info.Symbol is IMethodSymbol direct)
            yield return direct;
        foreach (var candidate in info.CandidateSymbols)
        {
            if (candidate is IMethodSymbol method)
                yield return method;
        }
    }

    // ---- fixed 节点 ----

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;

        // 资源指针局部的伴生语句按锚点（原声明语句）注入同一块列表。
        // 子语句访问与列表一一对应（每个语句 Visit 仍返回单个语句节点），
        // 故按下标对齐插入。
        if (!_blockInjections.TryGetValue(node, out var injections))
            return visited;

        var statements = new List<StatementSyntax>();
        for (var i = 0; i < node.Statements.Count; i++)
        {
            if (injections.TryGetValue(node.Statements[i], out var extras))
                statements.AddRange(extras);
            statements.Add(visited.Statements[i]);
        }

        return visited.WithStatements(List(statements));
    }

    public override SyntaxNode? VisitFixedStatement(FixedStatementSyntax node)
    {
        _statistics.FixedStatementsCount++;

        var records = new List<FrameRecord>();
        foreach (var declarator in node.Declaration.Variables)
        {
            var ptrSymbol = (_resolver.Resolve(declarator) is { } originalDeclarator
                ? _model.GetDeclaredSymbol(originalDeclarator)
                : null) as ILocalSymbol;
            var (kind, payload) = Classify(declarator.Initializer.Value);
            var suffix = kind == InitKind.Scalar ? "slot" : "array";
            var arrayName = MakeGeneratedName(declarator.Identifier.ValueText, suffix, node);
            records.Add(new FrameRecord(declarator, ptrSymbol, kind, arrayName, payload));

            if (kind != InitKind.Error)
            {
                CountKind(kind);
            }
        }

        // 先注册（含根符号=自身），再改写块体。
        foreach (var record in records)
        {
            if (record.Symbol == null || record.Kind == InitKind.Error) continue;
            _infos[record.Symbol] = new PtrInfo
            {
                ArrayName = record.ArrayName,
                SourceSlot = ResolveFrameSlot(
                    record.Kind, record.ArrayName, record.Payload,
                    record.Declarator.Identifier.ValueText),
                ReadOnlyString = record.Kind == InitKind.String,
                NullCompanionArray = record.Kind == InitKind.Null
                    ? record.ArrayName
                    : null,
            };
        }

        var body = (StatementSyntax)Visit(node.Statement)!;

        var statements = new List<StatementSyntax>();
        var writebacks = new List<StatementSyntax>();

        var lineStart = LineStartTrivia(node);

        foreach (var record in records)
        {
            var elementType = ((PointerTypeSyntax)node.Declaration.Type).ElementType;
            var ptrName = record.Declarator.Identifier.ValueText;

            switch (record.Kind)
            {
                case InitKind.Array:
                    var arrayExpr = (ExpressionSyntax)record.Payload!;
                    statements.Add(ArrayLocal(record.ArrayName, elementType, arrayExpr, node));
                    statements.Add(IndexLocal(ptrName, ZeroLiteral(), lineStart));
                    break;

                case InitKind.Offset:
                    var offset = (OffsetPayload)record.Payload!;
                    statements.Add(ArrayLocal(record.ArrayName, elementType, offset.Receiver, node));
                    statements.Add(IndexLocal(ptrName, offset.Index, lineStart));
                    break;

                case InitKind.Alias:
                    var alias = (AliasPayload)record.Payload!;
                    statements.Add(ArrayLocal(record.ArrayName, elementType,
                        IdentifierName(alias.SourceArray), node));
                    statements.Add(IndexLocal(ptrName, alias.Index, lineStart));
                    break;

                case InitKind.Null:
                    statements.Add(ArrayLocal(record.ArrayName, elementType,
                        PostfixUnaryExpression(
                            SyntaxKind.SuppressNullableWarningExpression,
                            LiteralExpression(SyntaxKind.NullLiteralExpression)), node));
                    statements.Add(IndexLocal(ptrName, ZeroLiteral(), lineStart));
                    break;

                case InitKind.String:
                    var sourceExpr = (ExpressionSyntax)record.Payload!;
                    var charType = PredefinedType(Token(SyntaxKind.CharKeyword));
                    // 源字符串先物化为临时局部：既保证源表达式单次求值，也避免
                    // 把字符串标识符放进拼接/实参位后被 R6 误按同名指针游标折叠
                    // （Uri.ParseScheme 的字符串 uriString 与配对指针形参同名）。
                    var sourceName = MakeGeneratedName(ptrName, "str", node);
                    statements.Add(LocalDeclarationStatement(VariableDeclaration(
                            PredefinedType(Token(SyntaxKind.StringKeyword)),
                            SingletonSeparatedList(VariableDeclarator(sourceName)
                                .WithInitializer(EqualsValueClause(sourceExpr)))))
                        .WithLeadingTrivia(LineStartTrivia(node)));
                    // C# 指针式固定字符串的合法尾读是 NUL 终止符（str[Length]
                    // 返回 '\0'）；ToCharArray() 只给出 Length 个元素，故物化
                    // Length+1 的数组并拷入原字符、末位留 '\0'，保持越界一位读
                    // '\0' 的原语义（Uri.CheckCanonical 的 str[i + 1] 场景）。
                    // 仅 string 起源（本分支）补位；数组/偏移/别名物化是对调用方
                    // 真实数组的别名，不得改变长度。
                    var paddedLength = BinaryExpression(
                        SyntaxKind.AddExpression,
                        MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            IdentifierName(sourceName),
                            IdentifierName("Length")),
                        LiteralExpression(
                            SyntaxKind.NumericLiteralExpression, Literal(1)));
                    statements.Add(ArrayLocal(record.ArrayName, charType,
                        ArrayCreationExpression(ArrayType(charType)
                            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier(
                                SingletonSeparatedList<ExpressionSyntax>(paddedLength))))),
                        node));
                    statements.Add(ExpressionStatement(InvocationExpression(
                            MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                IdentifierName(sourceName),
                                IdentifierName("CopyTo")))
                        .WithArgumentList(ArgumentList(SeparatedList(new[]
                        {
                            Argument(ZeroLiteral()),
                            Argument(IdentifierName(record.ArrayName)),
                            Argument(ZeroLiteral()),
                            Argument(MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                IdentifierName(sourceName),
                                IdentifierName("Length"))),
                        })))).WithLeadingTrivia(lineStart));
                    statements.Add(IndexLocal(ptrName, ZeroLiteral(), lineStart));
                    break;

                case InitKind.Scalar:
                    var target = (ExpressionSyntax)record.Payload!;
                    statements.Add(ArrayLocal(record.ArrayName, elementType,
                        ArrayCreationExpression(
                            ArrayType(elementType)
                                .WithRankSpecifiers(SingletonList(ArrayRankSpecifier())))
                        .WithInitializer(InitializerExpression(
                            SyntaxKind.ArrayInitializerExpression,
                            SingletonSeparatedList(target))), node));
                    statements.Add(IndexLocal(ptrName, ZeroLiteral(), lineStart));
                    writebacks.Add(ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        target,
                        ElementAccessExpression(IdentifierName(record.ArrayName))
                            .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                                Argument(ZeroLiteral()))))))
                        .WithLeadingTrivia(lineStart));
                    break;

                case InitKind.Error:
                    // 诊断已记录；占位让遍历继续，文件最终回滚。
                    statements.Add(EmptyStatement());
                    break;
            }
        }

        if (body is BlockSyntax bodyBlock)
        {
            statements.AddRange(bodyBlock.Statements);
        }
        else
        {
            statements.Add(body);
        }

        statements.AddRange(writebacks);

        return Block(statements).WithAdditionalAnnotations(FixedReplacedAnnotation);
    }

    // ---- 初始式分类 ----

    private (InitKind Kind, object? Payload) Classify(ExpressionSyntax raw)
    {
        var e = UnwrapParenthesized(raw);

        if (e.IsKind(SyntaxKind.NullLiteralExpression))
            return (InitKind.Null, null);

        if (e is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression } address)
        {
            if (address.Operand is ElementAccessExpressionSyntax elementAccess)
            {
                var arg = elementAccess.ArgumentList.Arguments[0].Expression;

                if (TryResolvePointer(elementAccess.Expression,
                        out var trackedArray, out var receiverIndex, out var trackedReadOnly))
                {
                    var index = BinaryExpression(
                        SyntaxKind.AddExpression,
                        ParenthesizedExpression(receiverIndex),
                        ParenthesizedExpression(arg));
                    return (InitKind.Alias,
                        new AliasPayload(trackedArray, index, trackedReadOnly));
                }

                var receiverType = TypeInfoFor(elementAccess.Expression).Type;
                var receiverSymbol = GetIdentifierSymbol(elementAccess.Expression);
                if (receiverType is IArrayTypeSymbol
                    || (receiverSymbol is IFieldSymbol field && _rewrittenFields.Contains(field)))
                {
                    return (InitKind.Offset,
                        new OffsetPayload(elementAccess.Expression, arg));
                }

                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"fixed pointer initializer '{raw}' takes the address of an unprovable expression; " +
                    "only addresses of managed array elements are supported in this form.",
                    raw);
                return (InitKind.Error, null);
            }

            // &MemoryMarshal.GetReference(span)：probe 实测 invocation 返回 T、
            // 外层 & 产生 T*。span 底层可溯源到数组时归一（R3 默认地址分支会
            // 把 &invocation 误判为 Scalar，故在此专门拦截）。
            if (address.Operand is InvocationExpressionSyntax getReference
                && IsMemoryMarshalGetReference(getReference, out var spanArgument))
            {
                if (TryResolveSpanBacking(
                        spanArgument, out var backingArray, out var backingIndex))
                {
                    if (IsZeroLiteralExpression(backingIndex))
                        return (InitKind.Array, backingArray);

                    return (InitKind.Offset,
                        new OffsetPayload(backingArray, backingIndex));
                }

                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"fixed pointer initializer '{raw}' takes MemoryMarshal.GetReference of a span " +
                    "whose backing array cannot be proven (only spans constructed from an array, " +
                    "array.AsSpan(...), MemoryMarshal.CreateSpan over an array element, or a " +
                    "stackalloc span are supported); the original file was preserved.",
                    raw);
                return (InitKind.Error, null);
            }

            // &scalar：局部/字段标量单槽。
            return (InitKind.Scalar, address.Operand);
        }

        // tracked pointer（标识符或纯指针算术表达式）→ 别名
        if (TryResolvePointer(e, out var aliasArray, out _, out var aliasReadOnly))
        {
            return (InitKind.Alias,
                new AliasPayload(aliasArray, e, aliasReadOnly));
        }

        // R2 改写过的 fixed 字段（模型仍为旧指针类型）→ 数组
        if (GetIdentifierSymbol(e) is IFieldSymbol rewrittenField
            && _rewrittenFields.Contains(rewrittenField))
        {
            return (InitKind.Array, e);
        }

        // R5 已把指针形参/局部改写为数组（模型仍为旧指针类型）→ 数组路径。
        if (GetIdentifierSymbol(e) is { } rewrittenSymbol
            && _pointerRewritten.Contains(rewrittenSymbol))
        {
            return (InitKind.Array, e);
        }

        // R5 改写符号的简单指针偏移（p + k / p - k）→ 数组 + 偏移索引。
        if (TryClassifyRewrittenOffset(e, out var rewrittenOffset))
        {
            return (InitKind.Offset, rewrittenOffset);
        }

        var type = TypeInfoFor(e).Type;
        if (type?.SpecialType == SpecialType.System_String)
            return (InitKind.String, e);
        if (type is IArrayTypeSymbol)
            return (InitKind.Array, e);

        AddError(
            UnsafeFfmDiagnosticCode.UF020,
            $"fixed pointer initializer '{raw}' does not originate from a provable managed array, " +
            "array element address, pointer alias, string or scalar slot; the original file was preserved.",
            raw);
        return (InitKind.Error, null);
    }

    /// <summary>
    /// 识别「R5 改写符号 ± 简单偏移」：<c>fixed(T* q = p + 2)</c> 中 p 已被
    /// R5 改写为数组，语义等价于「数组 p、索引 2」（减法索引取负）。
    /// </summary>
    private bool TryClassifyRewrittenOffset(
        ExpressionSyntax raw, out OffsetPayload? payload)
    {
        payload = null;
        if (raw is not BinaryExpressionSyntax binary) return false;
        if (!binary.IsKind(SyntaxKind.AddExpression)
            && !binary.IsKind(SyntaxKind.SubtractExpression))
        {
            return false;
        }

        var leftType = TypeInfoFor(binary.Left).Type;
        var rightType = TypeInfoFor(binary.Right).Type;

        ExpressionSyntax pointerSide;
        ExpressionSyntax offsetSide;
        var subtract = false;

        if (leftType is IPointerTypeSymbol)
        {
            pointerSide = binary.Left;
            offsetSide = binary.Right;
            subtract = binary.IsKind(SyntaxKind.SubtractExpression);
        }
        else if (rightType is IPointerTypeSymbol && binary.IsKind(SyntaxKind.AddExpression))
        {
            pointerSide = binary.Right;
            offsetSide = binary.Left;
        }
        else
        {
            return false;
        }

        if (GetIdentifierSymbol(pointerSide) is { } sourceSymbol
            && _pointerRewritten.Contains(sourceSymbol))
        {
            ExpressionSyntax index = offsetSide;
            if (subtract)
            {
                index = PrefixUnaryExpression(
                    SyntaxKind.UnaryMinusExpression, offsetSide);
            }

            payload = new OffsetPayload(pointerSide, index);
            return true;
        }

        return false;
    }

    // ---- GetReference(span) 数组源溯源 ----

    /// <summary>
    /// 是否 <c>System.Runtime.InteropServices.MemoryMarshal.GetReference</c> 单参调用；
    /// 命中则返回其 span 参数。
    /// </summary>
    private bool IsMemoryMarshalGetReference(
        InvocationExpressionSyntax invocation, out ExpressionSyntax spanArgument)
    {
        spanArgument = null!;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.ValueText != "GetReference")
        {
            return false;
        }

        if (SymbolInfoFor(memberAccess).Symbol is not IMethodSymbol method
            || method.ContainingType.ToDisplayString()
                != "System.Runtime.InteropServices.MemoryMarshal")
        {
            return false;
        }

        if (invocation.ArgumentList.Arguments.Count != 1) return false;

        spanArgument = invocation.ArgumentList.Arguments[0].Expression;
        return true;
    }

    /// <summary>
    /// 把 span 值溯源到「数组表达式 + 起始索引」。支持：数组直传（隐式转换）、
    /// <c>new Span/RReadOnlySpan(arr[, start])</c>、数组直接赋值给 span 局部、
    /// <c>arr.AsSpan([start])</c>、<c>MemoryMarshal.CreateSpan(ref arr[i], n)</c>、
    /// stackalloc span 局部（R4 随后把该局部变为数组，名字保留）。
    /// 参数/字段 span 与不可证构造返回 false。
    /// </summary>
    private bool TryResolveSpanBacking(
        ExpressionSyntax rawSpan,
        out ExpressionSyntax backingArray,
        out ExpressionSyntax backingIndex)
    {
        backingArray = null!;
        backingIndex = null!;

        var span = UnwrapParenthesized(rawSpan);

        // 数组直传（含隐式转换）。
        if (TypeInfoFor(span).Type is IArrayTypeSymbol)
        {
            backingArray = span;
            backingIndex = ZeroLiteral();
            return true;
        }

        // 内联 new Span<T>(arr[, start]) / AsSpan() / CreateSpan()。
        if (TryResolveSpanValue(span, out backingArray, out backingIndex))
            return true;

        // span 局部：看其声明初始式。
        if (GetIdentifierSymbol(span) is ILocalSymbol localSymbol)
        {
            var declarator = localSymbol.DeclaringSyntaxReferences
                .FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
            if (declarator?.Initializer?.Value is { } initializer)
            {
                // stackalloc span 局部：R4 链位在后，会把它变为同名数组局部。
                var unwrappedInitializer = UnwrapParenthesized(initializer);
                if (unwrappedInitializer is StackAllocArrayCreationExpressionSyntax
                    or ImplicitStackAllocArrayCreationExpressionSyntax)
                {
                    backingArray = IdentifierName(localSymbol.Name);
                    backingIndex = ZeroLiteral();
                    return true;
                }

                // 数组直接赋值（Span<T> s = arr;）。
                if (TypeInfoFor(unwrappedInitializer).Type is IArrayTypeSymbol)
                {
                    backingArray = unwrappedInitializer;
                    backingIndex = ZeroLiteral();
                    return true;
                }

                if (TryResolveSpanValue(
                        unwrappedInitializer, out backingArray, out backingIndex))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>解析「直接产生 span 的值表达式」：ctor/AsSpan/CreateSpan。</summary>
    private bool TryResolveSpanValue(
        ExpressionSyntax value,
        out ExpressionSyntax backingArray,
        out ExpressionSyntax backingIndex)
    {
        backingArray = null!;
        backingIndex = null!;

        if (value is ObjectCreationExpressionSyntax creation)
        {
            var arguments = creation.ArgumentList?.Arguments
                .Select(a => a.Expression).ToArray() ?? Array.Empty<ExpressionSyntax>();

            switch (arguments.Length)
            {
                case 1 when TypeInfoFor(arguments[0]).Type is IArrayTypeSymbol:
                    backingArray = arguments[0];
                    backingIndex = ZeroLiteral();
                    return true;

                case 2 when TypeInfoFor(arguments[0]).Type is IArrayTypeSymbol:
                    backingArray = arguments[0];
                    backingIndex = arguments[1];
                    return true;
            }

            return false;
        }

        if (value is InvocationExpressionSyntax invocation
            && invocation.Expression is MemberAccessExpressionSyntax invokedMember)
        {
            // arr.AsSpan([start])
            if (invokedMember.Name.Identifier.ValueText == "AsSpan"
                && TypeInfoFor(invokedMember.Expression).Type is IArrayTypeSymbol)
            {
                var args = invocation.ArgumentList.Arguments;
                if (args.Count == 0)
                {
                    backingArray = invokedMember.Expression;
                    backingIndex = ZeroLiteral();
                    return true;
                }

                if (args.Count == 1)
                {
                    backingArray = invokedMember.Expression;
                    backingIndex = args[0].Expression;
                    return true;
                }
            }

            // MemoryMarshal.CreateSpan(ref arr[i], length)
            if (invokedMember.Name.Identifier.ValueText == "CreateSpan"
                && SymbolInfoFor(invokedMember).Symbol is IMethodSymbol createMethod
                && createMethod.ContainingType.ToDisplayString()
                    == "System.Runtime.InteropServices.MemoryMarshal"
                && invocation.ArgumentList.Arguments.Count >= 1)
            {
                var first = UnwrapParenthesized(
                    invocation.ArgumentList.Arguments[0].Expression);
                if (first is PrefixUnaryExpressionSyntax {
                        RawKind: (int)SyntaxKind.AddressOfExpression } address
                    && address.Operand is ElementAccessExpressionSyntax elementAccess
                    && elementAccess.ArgumentList.Arguments.Count == 1
                    && TypeInfoFor(elementAccess.Expression).Type is IArrayTypeSymbol)
                {
                    backingArray = elementAccess.Expression;
                    backingIndex = elementAccess.ArgumentList.Arguments[0].Expression;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsZeroLiteralExpression(ExpressionSyntax expression)
        => UnwrapParenthesized(expression)
            is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
            && literal.Token.ValueText == "0";

    // ---- 块体表达式 ----

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Kind() == SyntaxKind.PointerIndirectionExpression)
        {
            // *p++ / *++p / *p-- / *--p：剥掉自增层，操作数为 R5 改写符号时
            // 整体交 R6（在索引局部上原地自增，保持短路），R3 不前置 UF020。
            var mutationInner = UnwrapParenthesized(node.Operand);
            var isMutation = false;
            switch (mutationInner)
            {
                case PostfixUnaryExpressionSyntax post
                    when post.Kind() is SyntaxKind.PostIncrementExpression
                        or SyntaxKind.PostDecrementExpression:
                    mutationInner = UnwrapParenthesized(post.Operand);
                    isMutation = true;
                    break;
                case PrefixUnaryExpressionSyntax pre
                    when pre.Kind() is SyntaxKind.PreIncrementExpression
                        or SyntaxKind.PreDecrementExpression:
                    mutationInner = UnwrapParenthesized(pre.Operand);
                    isMutation = true;
                    break;
            }

            if (isMutation
                && GetIdentifierSymbol(mutationInner) is { } mutatedSymbol
                && (_pointerRewritten.Contains(mutatedSymbol)
                    || IsTrackedResolved(mutatedSymbol)))
            {
                return node;
            }

            // R5 已把操作数改写为数组：使用点归 R6 索引化，本规则不拦截。
            if (GetIdentifierSymbol(node.Operand) is { } rewrittenOperand
                && _pointerRewritten.Contains(rewrittenOperand))
            {
                return node;
            }

            // 标量槽重解释 *((T*)&scalar)：结构与配对由 catalog 统一匹配，
            // R6 归一为 System.BitConverter 调用，R3 不前置 UF020。
            if (PointerMethodCatalog.TryMatchScalarReinterpret(
                    node.Operand, _model, _resolver, out _, out _))
            {
                return node;
            }

            // 重解释存储形参上的只读打包读 *p / *(p±k) / *(T3*)(p±k)：
            // R6 按 StorageElement 计划在异类存储数组上小端拼包，R3 不前置
            // UF020（亦不遍历子 cast——形态由 R6 在解引用点整体接管）。
            if (PointerMethodCatalog.MatchesReinterpretStorageRead(
                    node.Operand, _model, _resolver,
                    ReinterpretPlanForNode(node)))
            {
                return node;
            }

            if (TryResolvePointer(node.Operand, out var array, out var index, out var readOnly))
            {
                if (readOnly && IsWriteTarget(node))
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF030,
                        $"write through fixed string pointer '{node}' is undefined and cannot be " +
                        "written back; the fixed string buffer is read-only.",
                        node);
                    return node;
                }

                return ElementAccessExpression(IdentifierName(array))
                    .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                        Argument(index))))
                    .WithAdditionalAnnotations(FixedIndexedAnnotation);
            }

            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"cannot statically prove what pointer expression '{node}' points to; the original " +
                "file was preserved.",
                node);
            return node;
        }

        if (node.Kind() == SyntaxKind.AddressOfExpression)
        {
            // 被指针局部声明/赋值拦截时本节点不会被访问到；落到此处即逃逸。
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"address-of expression '{node}' escapes the supported fixed forms (it must be a " +
                "fixed statement initializer); the original file was preserved.",
                node);
            return node;
        }

        return base.VisitPrefixUnaryExpression(node);
    }

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        var receiverType = TypeInfoFor(node.Expression).Type;
        if (receiverType is IPointerTypeSymbol)
        {
            // R2 已改写为数组字段的 fixed 缓冲区：buf[i] 语法对数组天然合法，保持原样。
            if (GetIdentifierSymbol(node.Expression) is IFieldSymbol rewrittenReceiver
                && _rewrittenFields.Contains(rewrittenReceiver))
            {
                return base.VisitElementAccessExpression(node);
            }

            // R5 已改写为数组的指针形参/局部：p[i] 归 R6 处理，保持节点原样。
            if (GetIdentifierSymbol(node.Expression) is { } rewrittenPointerReceiver
                && _pointerRewritten.Contains(rewrittenPointerReceiver))
            {
                return base.VisitElementAccessExpression(node);
            }

            if (node.ArgumentList.Arguments.Count != 1)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer element access '{node}' has an unsupported argument list.",
                    node);
                return node;
            }

            if (TryResolvePointer(node.Expression, out var array, out var receiverIndex, out var readOnly))
            {
                var arg = node.ArgumentList.Arguments[0].Expression;

                if (readOnly && IsWriteTarget(node))
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF030,
                        $"write through fixed string pointer '{node}' is undefined and cannot be " +
                        "written back; the fixed string buffer is read-only.",
                        node);
                    return node;
                }

                var index = BinaryExpression(
                    SyntaxKind.AddExpression,
                    ParenthesizedExpression(receiverIndex),
                    ParenthesizedExpression(arg));

                return ElementAccessExpression(IdentifierName(array))
                    .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                        Argument(index))))
                    .WithAdditionalAnnotations(FixedIndexedAnnotation);
            }

            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"cannot statically prove what pointer expression '{node.Expression}' points to; " +
                "the original file was preserved.",
                node);
            return node;
        }

        return base.VisitElementAccessExpression(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (node.Kind() == SyntaxKind.PointerMemberAccessExpression)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"struct pointer member access '{node}' (p->m) cannot be normalized in the fixed " +
                "rewriter; the original file was preserved.",
                node);
            return base.VisitMemberAccessExpression(node);
        }

        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // 向 R5 降级的只读资源指针字段存储：R5 已证明该字段全部写入只来自
        // 同集合字段、单次构造器形参或资源流 PositionPointer 局部。
        if (node.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && GetIdentifierSymbol(node.Left) is IFieldSymbol targetField
            && _rewrittenFields.Contains(targetField))
        {
            var fieldRhs = UnwrapParenthesized(node.Right);
            if (!fieldRhs.IsKind(SyntaxKind.NullLiteralExpression)
                && TypeInfoFor(fieldRhs).Type is IPointerTypeSymbol)
            {
                // R5 已把 RHS（形参/字段）改写为托管数组：普通数组引用赋值。
                var rhsSymbol = GetIdentifierSymbol(fieldRhs);
                if (rhsSymbol != null && _pointerRewritten.Contains(rhsSymbol))
                    return base.VisitAssignmentExpression(node);

                // R3 已降级的资源指针局部：RHS 归原为伴生数组引用。
                if (TryResolvePointer(fieldRhs, out var resourceArray, out _, out _))
                {
                    var fieldLeft = (ExpressionSyntax)Visit(node.Left)!;
                    return node.Update(fieldLeft, node.OperatorToken,
                        IdentifierName(resourceArray));
                }

                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"cannot assign pointer value '{node.Right}' to resource field " +
                    $"'{node.Left}': its origin is not a proven resource stream array or another " +
                    "proven source; the original file was preserved.",
                    node);
                return base.VisitAssignmentExpression(node);
            }
        }

        // 赋给已登记指针局部（含声明后赋值的别名）且 RHS 为指针形态。
        if (GetIdentifierSymbol(node.Left) is ILocalSymbol leftSymbol
            && _infos.TryGetValue(leftSymbol, out var info))
        {
            var rhs = UnwrapParenthesized(node.Right);
            var rhsType = TypeInfoFor(node.Right).Type;

            if (rhsType is IPointerTypeSymbol
                || rhs.IsKind(SyntaxKind.AddressOfExpression)
                || rhs.IsKind(SyntaxKind.NullLiteralExpression))
            {
                ExpressionSyntax newRhs;

                if (rhs.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    newRhs = ZeroLiteral();
                    info.ArrayName = MakeGeneratedName(leftSymbol.Name, "array", node);
                    info.Resolved = true;
                    if (info.NullCompanionArray != null)
                        InjectCompanionArraySync(node, info.NullCompanionArray, null);
                }
                else if (TryResolvePointer(rhs, out var sourceArray, out var indexExpression, out _))
                {
                    newRhs = indexExpression;
                    info.ArrayName = sourceArray;
                    // 重绑定到另一可证游标：源槽随 RHS 真源迁移，后续比较/
                    // 相减按新槽判定同源。
                    info.SourceSlot = CompanionSlot(sourceArray);
                    info.Resolved = true;
                    if (info.NullCompanionArray != null)
                        InjectCompanionArraySync(
                            node, info.NullCompanionArray, IdentifierName(sourceArray));
                }
                else if (TryResolvePinnedArrayPointer(
                             rhs, node, out var repinnedArray, out var repinnedIndex,
                             out var repinnedSlot)
                    && repinnedArray == info.ArrayName)
                {
                    // 同名数组变量上的重固定（数组变量可经 newDest 重绑定，
                    // 按名引用保持动态正确）：游标归零，数组名不变，源槽随
                    // 本次 Alloc 的数组真源更新。
                    newRhs = repinnedIndex;
                    info.SourceSlot = repinnedSlot;
                    if (info.NullCompanionArray != null)
                        InjectCompanionArraySync(
                            node, info.NullCompanionArray, IdentifierName(info.ArrayName));
                }
                else if (IsPairedPointerReturnInvocation(rhs))
                {
                    // 配对指针返回方法调用：结果按配对约定是同源 int 索引，
                    // 实参展开/调用重建全部归 R6；R3 不前置 UF020。
                    var pairedLeft = (ExpressionSyntax)Visit(node.Left)!;
                    return node.Update(pairedLeft, node.OperatorToken, node.Right);
                }
                else
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF020,
                        $"cannot assign pointer value '{node.Right}' to local '{leftSymbol.Name}': " +
                        "its origin is not provable; the original file was preserved.",
                        node);
                    return base.VisitAssignmentExpression(node);
                }

                var left = (ExpressionSyntax)Visit(node.Left)!;
                return node.Update(left, node.OperatorToken, newRhs);
            }
        }

        var visitedLeft = (ExpressionSyntax)Visit(node.Left)!;
        var visitedRight = (ExpressionSyntax)Visit(node.Right)!;
        if (visitedLeft == node.Left && visitedRight == node.Right) return node;
        return node.Update(visitedLeft, node.OperatorToken, visitedRight);
    }

    /// <summary>
    /// null 初值游标与 null 的比较（<c>p == null</c>/<c>p != null</c>）：指针的
    /// 空状态由伴生数组承载，归一为 <c>_p_array == null</c>/<c>_p_array != null</c>
    /// （Uri.UnescapeOnly 的 <c>pnew == null</c> 场景）；其余二元表达式原样下探。
    /// </summary>
    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (node.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
        {
            var left = UnwrapParenthesized(node.Left);
            var right = UnwrapParenthesized(node.Right);

            var pointerSide = left.IsKind(SyntaxKind.NullLiteralExpression)
                ? right
                : right.IsKind(SyntaxKind.NullLiteralExpression)
                    ? left
                    : null;

            if (pointerSide != null
                && GetIdentifierSymbol(pointerSide) is ILocalSymbol local
                && _infos.TryGetValue(local, out var info)
                && info.NullCompanionArray != null)
            {
                return BinaryExpression(
                    node.Kind(),
                    IdentifierName(info.NullCompanionArray),
                    LiteralExpression(SyntaxKind.NullLiteralExpression));
            }
        }

        return base.VisitBinaryExpression(node);
    }

    /// <summary>
    /// null 初值游标的伴生数组同步：指针赋值归一为「数组 + int 索引」后，指针的
    /// 空/非空状态由伴生数组承载，故在该赋值语句前注入
    /// <c>_p_array = &lt;新数组&gt;</c>（赋空指针则注入 <c>_p_array = null!;</c>）。
    /// 仅当赋值直接位于块语句列表时注入；其它形态保守跳过（不误改写，仅极端
    /// 站点下空判定语义不精确）。
    /// </summary>
    private void InjectCompanionArraySync(
        AssignmentExpressionSyntax node, string arrayName, ExpressionSyntax? arrayExpression)
    {
        if (node.Parent is not ExpressionStatementSyntax statement
            || statement.Parent is not BlockSyntax block)
        {
            return;
        }

        var rhs = arrayExpression ?? PostfixUnaryExpression(
            SyntaxKind.SuppressNullableWarningExpression,
            LiteralExpression(SyntaxKind.NullLiteralExpression));
        var sync = ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(arrayName), rhs))
            .WithLeadingTrivia(LineStartTrivia(statement));
        InjectBeforeStatement(block, statement, sync);
    }

    public override SyntaxNode? VisitArgument(ArgumentSyntax node)
    {
        var expression = UnwrapParenthesized(node.Expression);

        // R5 已改写为数组的标识符实参：数组实参是正常托管调用，不逃逸。
        if (GetIdentifierSymbol(expression) is { } rewrittenArgument
            && _pointerRewritten.Contains(rewrittenArgument))
        {
            return base.VisitArgument(node);
        }

        if (TypeInfoFor(node.Expression).Type is IPointerTypeSymbol
            && !(expression is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression })
            // 指针→指针强转实参（如 (byte*)p）：VisitCastExpression 已就绝对地址
            // 强转发 UF021，或按 R5 改写域交 R6；此处不再重复发逃逸 UF020。
            && !(expression is CastExpressionSyntax pointerCast
                && TypeInfoFor(pointerCast.Expression).Type is IPointerTypeSymbol)
            // 可解析为「数组+int 索引」的 tracked 指针：归一后实参是 int，不是逃逸。
            && !TryResolvePointer(node.Expression, out _, out _, out _))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer expression '{node.Expression}' is passed as an argument and escapes the " +
                "fixed block; the original file was preserved.",
                node);
        }

        return base.VisitArgument(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression != null)
        {
            var expression = UnwrapParenthesized(node.Expression);

            // R5 已改写为数组的标识符返回：数组返回是正常托管返回。
            if (GetIdentifierSymbol(expression) is { } rewrittenReturn
                && _pointerRewritten.Contains(rewrittenReturn))
            {
                return base.VisitReturnStatement(node);
            }

            // 配对方法：指针返回按约定归一为 int 索引返回（形参索引、tracked
            // 索引表达式或配对调用结果），具体重建归 R6。
            if (_pairedStack.Count > 0
                && TypeInfoFor(node.Expression).Type is IPointerTypeSymbol)
            {
                return base.VisitReturnStatement(node);
            }

            if (TypeInfoFor(node.Expression).Type is IPointerTypeSymbol)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer expression '{node.Expression}' is returned and escapes the fixed " +
                    "block; the original file was preserved.",
                    node);
            }
        }

        return base.VisitReturnStatement(node);
    }

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        if (TypeInfoFor(node.Expression).Type is IPointerTypeSymbol)
        {
            // R5 改写符号上的强转：归一后操作数是数组，强转交给 R6 按
            // UF021 规则判定，本规则不前置拒绝。
            var castSymbol = GetIdentifierSymbol(node.Expression);
            if (castSymbol != null && _pointerRewritten.Contains(castSymbol))
                return base.VisitCastExpression(node);

            // 重解释存储读 *(T3*)(p ± k) 与重解释实参 (T2*)(p ± k)：形参/被调
            // 方配对计划带 StorageElement 时由 R6 统一索引化（小端打包读/
            // 数组+索引实参展开），R3 不前置 UF021。
            if (PointerMethodCatalog.MatchesReinterpretStorageRead(
                    node, _model, _resolver, ReinterpretPlanForNode(node))
                || _catalog.IsReinterpretArgumentCast(node, _model, _resolver))
            {
                return node;
            }

            // R3 已降级 fixed 指针局部（含其别名局部）：归一后 p 只是 int 索引，
            // 对其取整等于把相对索引当绝对地址（HexConverter (IntPtr)p 场景），
            // 显式 UF021，不放行无意义强转。
            AddError(
                UnsafeFfmDiagnosticCode.UF021,
                $"cast involving pointer expression '{node.Expression}' takes an absolute pointer " +
                "address (or changes its pointer kind); only a same-array pointer difference is " +
                "supported, and the cast is never passed through unchanged.",
                node);
        }

        return base.VisitCastExpression(node);
    }

    // ---- Buffer.MemoryCopy(void*, void*, ulong, ulong)：数组间字节拷贝 ----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (TryLowerBufferMemoryCopy(node, out var blockCopy))
            return blockCopy;

        return base.VisitInvocationExpression(node);
    }

    /// <summary>
    /// <c>Buffer.MemoryCopy(src, dst, destinationSizeInBytes, sourceBytesToCopy)</c>
    /// 的两个 void* 实参都可溯源到「托管数组 + 元素索引」（fixed 帧/别名/
    /// R5 flow/GCHandle Pinned 句柄）时，归一为
    /// <c>Buffer.BlockCopy(srcArray, srcByteOffset, dstArray, dstByteOffset,
    /// sourceBytesToCopy)</c>：指针实参上的 byte* 重解释强转（char*→byte*
    /// 等）只是元素视角变化，字节偏移 = 元素索引 × 元素尺寸。
    /// destinationSizeInBytes 仅用于 MemoryCopy 的越界报错，BlockCopy 按
    /// 目标数组实际长度校验（数组身份已证，二者契约一致），故丢弃。
    /// 任一侧不可溯源即返回 false，落既有 UF021/UF020 判决，绝不放行。
    /// </summary>
    private bool TryLowerBufferMemoryCopy(
        InvocationExpressionSyntax node,
        out InvocationExpressionSyntax blockCopy)
    {
        blockCopy = null!;

        if (node.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.ValueText != "MemoryCopy"
            || SymbolInfoFor(memberAccess).Symbol is not IMethodSymbol method
            || method.ContainingType.ToDisplayString() != "System.Buffer"
            || node.ArgumentList.Arguments.Count != 4)
        {
            return false;
        }

        var arguments = node.ArgumentList.Arguments;
        if (arguments.Any(a => !a.RefKindKeyword.IsKind(SyntaxKind.None)))
            return false;

        if (!TryResolveMemoryCopyOperand(
                UnwrapParenthesized(arguments[0].Expression), node,
                out var sourceArray, out var sourceByteOffset, destination: false)
            || !TryResolveMemoryCopyOperand(
                UnwrapParenthesized(arguments[1].Expression), node,
                out var destinationArray, out var destinationByteOffset, destination: true))
        {
            return false;
        }

        var byteCount = UnwrapParenthesized(arguments[3].Expression);
        if (TypeInfoFor(byteCount).Type is { } countType
            && countType.SpecialType != SpecialType.System_Int32)
        {
            byteCount = CastExpression(
                PredefinedType(Token(SyntaxKind.IntKeyword)),
                ParenthesizedExpression(byteCount));
        }

        blockCopy = InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    ParseExpression("global::System.Buffer"),
                    IdentifierName("BlockCopy")))
            .WithArgumentList(ArgumentList(SeparatedList(new[]
            {
                Argument(sourceArray),
                Argument(sourceByteOffset),
                Argument(destinationArray),
                Argument(destinationByteOffset),
                Argument(byteCount),
            })));

        _statistics.MemoryCopiesLowered++;
        return true;
    }

    /// <summary>
    /// 解析 MemoryCopy 的单个指针实参为「数组 + 字节偏移」。先剥掉任意层
    /// 指针→指针重解释强转（<c>(byte*)p</c>、<c>(void*)(byte*)p</c>），
    /// 再按 R3 既有数组+索引模型（含 R5 flow 与 GCHandle Pinned 句柄）溯源；
    /// 字节偏移按最内层指针元素尺寸放大。目标实参溯源到只读 fixed string
    /// 时发 UF030（写入只读缓冲未定义）。
    /// </summary>
    private bool TryResolveMemoryCopyOperand(
        ExpressionSyntax raw,
        SyntaxNode site,
        out ExpressionSyntax array,
        out ExpressionSyntax byteOffset,
        bool destination)
    {
        array = null!;
        byteOffset = null!;

        var inner = raw;
        while (inner is CastExpressionSyntax cast
            && cast.Type is PointerTypeSyntax
            && TypeInfoFor(cast.Expression).Type is IPointerTypeSymbol)
        {
            inner = UnwrapParenthesized(cast.Expression);
        }

        if (TypeInfoFor(inner).Type is not IPointerTypeSymbol innerPointer
            || !TryPrimitivePointerElementSize(innerPointer.PointedAtType, out var elementSize))
        {
            return false;
        }

        string? resolvedArrayName = null;
        ExpressionSyntax? elementIndex = null;
        var readOnly = false;

        if (TryResolvePointer(inner, out var trackedArray, out var trackedIndex, out var trackedReadOnly))
        {
            resolvedArrayName = trackedArray;
            elementIndex = trackedIndex;
            readOnly = trackedReadOnly;
        }
        else if (TryResolvePinnedArrayPointer(
                     inner, site, out var pinnedArray, out var pinnedIndex, out _))
        {
            resolvedArrayName = pinnedArray;
            elementIndex = pinnedIndex;
        }

        if (resolvedArrayName == null || elementIndex == null)
            return false;

        if (destination && readOnly)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF030,
                $"Buffer.MemoryCopy destination '{raw}' writes through a fixed string pointer; " +
                "the fixed string buffer is read-only and cannot be written back.",
                site);
            return false;
        }

        array = IdentifierName(resolvedArrayName);
        byteOffset = elementSize == 1
            ? elementIndex
            : BinaryExpression(
                SyntaxKind.MultiplyExpression,
                ParenthesizedExpression(elementIndex),
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(elementSize)));
        return true;
    }

    /// <summary>
    /// 基元指针元素的托管尺寸（字节）：仅支持可证明尺寸的基元元素；
    /// 枚举/非基元 struct/void 等返回 false（保守落 UF021）。
    /// </summary>
    private static bool TryPrimitivePointerElementSize(ITypeSymbol element, out int size)
    {
        switch (element.SpecialType)
        {
            case SpecialType.System_Boolean
                or SpecialType.System_Byte
                or SpecialType.System_SByte:
                size = 1;
                return true;
            case SpecialType.System_Char
                or SpecialType.System_Int16
                or SpecialType.System_UInt16:
                size = 2;
                return true;
            case SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Single:
                size = 4;
                return true;
            case SpecialType.System_Int64
                or SpecialType.System_UInt64
                or SpecialType.System_Double:
                size = 8;
                return true;
            default:
                size = 0;
                return false;
        }
    }

    // ---- 从 fixed 指针重建 Span：逃逸，UF022 ----

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        // byte*→char* 重解释局部唯一允许的消费形态 new String(p[, +k], 0, cch)：
        // UTF-16 字节经 Encoding.Unicode.GetString 解码。
        if (TryBuildUnicodeGetString(node, out var getStringCall))
            return getStringCall;

        var createdType = TypeInfoFor(node).Type;
        if (createdType is INamedTypeSymbol created
            && node.ArgumentList is { Arguments.Count: >= 1 } argumentList
            && IsSpanLikeType(created))
        {
            var firstArg = UnwrapParenthesized(argumentList.Arguments[0].Expression);
            var sourceSymbol = firstArg is CastExpressionSyntax castFromPointer
                ? GetIdentifierSymbol(UnwrapParenthesized(castFromPointer.Expression))
                : GetIdentifierSymbol(firstArg);

            // fixed 降级域内从指针（或指针强转）重建 Span/ReadOnlySpan：
            // 相对索引被当成地址跨结构逃逸，UF022 显式拒绝。
            if (sourceSymbol is ILocalSymbol reconstructedLocal
                && _infos.ContainsKey(reconstructedLocal))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF022,
                    $"span reconstruction '{node.ToString().Replace("\n", " ")}' rebuilds a " +
                    $"{created.Name} from a fixed pointer (index); the normalized pointer is only " +
                    "a relative array index and cannot be re-interpreted as an address outside " +
                    "the fixed block; the original file was preserved.",
                    node);
                return node;
            }
        }

        return base.VisitObjectCreationExpression(node);
    }

    private static bool IsSpanLikeType(INamedTypeSymbol type)
    {
        var display = type.ConstructedFrom.ToDisplayString();
        return display is "System.Span<T>" or "System.ReadOnlySpan<T>";
    }

    // ---- 块内指针局部声明 ----

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        var entries = node.Declaration.Variables
            .Select(v => (v, sym: (_resolver.Resolve(v) is { } originalV
                ? _model.GetDeclaredSymbol(originalV)
                : null) as ILocalSymbol))
            .ToList();

        if (!entries.Any(e => e.sym?.Type is IPointerTypeSymbol))
            return base.VisitLocalDeclarationStatement(node);

        // UF040 降级域：裸指针 stackalloc 单声明 → 同块前置伴生堆数组 +
        // 同名 int 索引局部（prescan 已完成外泄/同族安全判定）。
        if (entries.Count == 1
            && entries[0].sym is { } stackAllocSymbol
            && _lowerableStackAllocLocals.Contains(stackAllocSymbol))
        {
            return LowerStackAllocLocal(node, node.Declaration.Variables[0], stackAllocSymbol);
        }

        // R5 已改写的声明（当前树已是 byte[] q = p 形态）：其指针符号全部在
        // R5 集合内，本规则不能再按旧符号类型降级，否则会误报 UF020。
        if (entries.Where(e => e.sym?.Type is IPointerTypeSymbol)
                   .All(e => _pointerRewritten.Contains(e.sym!)))
        {
            return base.VisitLocalDeclarationStatement(node);
        }

        // 块内指针局部统一降级为「int 索引局部，原名沿用」（设计：tracked
        // 指针 p 在 fixed 块内由数组局部 _p_array + 同名 int 索引承载）。
        // 元素类型仅用于 null 初值时合成伴生数组局部。
        TypeSyntax elementTypeSyntax = node.Declaration.Type is PointerTypeSyntax pointer
            ? pointer.ElementType
            : ParseTypeName(
                ((IPointerTypeSymbol)entries.First(e => e.sym!.Type is IPointerTypeSymbol).sym!.Type)
                    .PointedAtType.ToDisplayString());

        // byte*→char* UTF-16 重解释单声明（fixed 域内）：声明整体移除，其
        // new String 实参用途归一为 Encoding.Unicode.GetString。
        if (entries.Count == 1
            && entries[0].sym is { } reinterpretSymbol
            && node.Declaration.Variables[0].Initializer?.Value is { } reinterpretRaw
            && TryRegisterUnicodeReinterpret(
                UnwrapParenthesized(reinterpretRaw),
                reinterpretSymbol,
                node,
                node.Declaration.Variables[0]))
        {
            return null;
        }

        var newDeclarators = new List<VariableDeclaratorSyntax>();
        var extraStatements = new List<StatementSyntax>();

        foreach (var (declarator, symbol) in entries)
        {
            if (symbol?.Type is not IPointerTypeSymbol)
            {
                newDeclarators.Add((VariableDeclaratorSyntax)Visit(declarator)!);
                continue;
            }

            var rawInitializer = declarator.Initializer?.Value;
            string? arrayName = null;
            string? sourceSlot = null;
            string? nullCompanionArray = null;
            ExpressionSyntax? newInitializer = null;

            if (rawInitializer != null)
            {
                var initializer = UnwrapParenthesized(rawInitializer);

                if (initializer.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    arrayName = MakeGeneratedName(declarator.Identifier.ValueText, "array", node);
                    sourceSlot = arrayName;
                    // null 初值游标：伴生数组的 null 状态即「指针为空」的托管
                    // 表示，登记供 p == null 归一与指针赋值同步改写使用。
                    nullCompanionArray = arrayName;
                    var nullArrayLocal = ArrayLocal(arrayName, elementTypeSyntax,
                        PostfixUnaryExpression(
                            SyntaxKind.SuppressNullableWarningExpression,
                            LiteralExpression(SyntaxKind.NullLiteralExpression)),
                        node);
                    // 伴生数组与同名 int 索引必须同处一块作用域：优先注入父块
                    // 语句列表（声明前的同一层），不能用嵌套块包裹——后续语句
                    // 仍要引用这两个名字。
                    if (node.Parent is BlockSyntax nullParentBlock)
                    {
                        InjectBeforeStatement(nullParentBlock, node, nullArrayLocal);
                    }
                    else
                    {
                        extraStatements.Add(nullArrayLocal);
                    }

                    newInitializer = ZeroLiteral();
                }
                else if (TryResolvePointer(initializer,
                             out var sourceArray, out var indexExpression, out _))
                {
                    arrayName = sourceArray;
                    sourceSlot = CompanionSlot(sourceArray);
                    newInitializer = indexExpression;
                }
                else if (TryResolvePinnedArrayPointer(
                             initializer, declarator,
                             out var pinnedArray, out var pinnedIndex, out var pinnedSlot))
                {
                    arrayName = pinnedArray;
                    sourceSlot = pinnedSlot;
                    newInitializer = pinnedIndex;
                }
                else if (TryResolveResourceStreamPointer(
                             initializer, symbol, out var resourceStream, out var resourceArray)
                    && !TryRegisterResourceArrayInjection(
                            node, elementTypeSyntax, resourceArray, resourceStream))
                {
                    // 诊断已发（声明不在块语句列表中）：占位让整文回滚。
                    newDeclarators.Add(declarator);
                    continue;
                }
                else if (resourceArray != null)
                {
                    arrayName = resourceArray;
                    // 物化副本各自独立，槽即伴生数组名。
                    sourceSlot = resourceArray;
                    newInitializer = ZeroLiteral();
                }
                else
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF020,
                        $"pointer local '{declarator.Identifier.ValueText}' is not initialized from a " +
                        "provable array/slot; the original file was preserved.",
                        declarator);
                    newDeclarators.Add(declarator);
                    continue;
                }
            }

            var rebuilt = declarator.WithInitializer(
                newInitializer == null ? null : EqualsValueClause(newInitializer));
            newDeclarators.Add(rebuilt);

            _infos[symbol] = new PtrInfo
            {
                ArrayName = arrayName ?? string.Empty,
                SourceSlot = sourceSlot ?? (arrayName ?? string.Empty),
                Resolved = arrayName != null,
                NullCompanionArray = nullCompanionArray,
            };
        }

        StatementSyntax newStatement = node
            .WithDeclaration(node.Declaration
                .WithType(PredefinedType(Token(SyntaxKind.IntKeyword)))
                .WithVariables(SeparatedList(newDeclarators)));

        if (extraStatements.Count > 0)
        {
            return Block(List(extraStatements).Add(newStatement));
        }

        return newStatement;
    }

    /// <summary>
    /// UF040 降级：<c>T* p = stackalloc T[n];</c> → 同块语句列表中原声明前
    /// 注入 <c>T[] _p_array = new T[n];</c>，声明本身变为 <c>int p = 0;</c>，
    /// 并登记 <see cref="_infos"/>（源槽即伴生数组）。R4 的 residual 闸门
    /// 天然通过：stackalloc 节点已物化进 new 数组表达式。
    /// </summary>
    private StatementSyntax LowerStackAllocLocal(
        LocalDeclarationStatementSyntax node,
        VariableDeclaratorSyntax declarator,
        ILocalSymbol symbol)
    {
        var rawInitializer = declarator.Initializer?.Value;
        var pointerSymbol = (IPointerTypeSymbol)symbol.Type;

        TypeSyntax elementTypeSyntax = node.Declaration.Type is PointerTypeSyntax pointerType
            ? pointerType.ElementType
            : ParseTypeName(pointerSymbol.PointedAtType.ToDisplayString());

        // checked(stackalloc T[n]) 与 new T[n] 负长度行为一致（均抛
        // OverflowException，不依赖溢出检查上下文），剥除 checked 包装。
        var unwrappedInitializer = rawInitializer!;
        while (true)
        {
            if (unwrappedInitializer is ParenthesizedExpressionSyntax parens)
            {
                unwrappedInitializer = parens.Expression;
                continue;
            }

            if (unwrappedInitializer is CheckedExpressionSyntax checkedExpression)
            {
                unwrappedInitializer = checkedExpression.Expression;
                continue;
            }

            break;
        }

        ExpressionSyntax newArray;
        if (unwrappedInitializer is StackAllocArrayCreationExpressionSyntax explicitStack)
        {
            if (explicitStack.Type is not ArrayTypeSyntax arrayType)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF040,
                    $"stackalloc expression '{explicitStack}' has an unrecognized type shape " +
                    "(internal rule gap).",
                    declarator);
                return node;
            }

            newArray = ArrayCreationExpression(arrayType)
                .WithInitializer(explicitStack.Initializer)
                .WithAdditionalAnnotations(StackAllocRewriter.StackAllocReplacedAnnotation);
            _statistics.ExplicitStackAllocCollected++;
        }
        else if (unwrappedInitializer is ImplicitStackAllocArrayCreationExpressionSyntax implicitStack)
        {
            var implicitArrayType = ArrayType(elementTypeSyntax)
                .WithRankSpecifiers(SingletonList(ArrayRankSpecifier()));
            newArray = ArrayCreationExpression(implicitArrayType)
                .WithInitializer(implicitStack.Initializer)
                .WithAdditionalAnnotations(StackAllocRewriter.StackAllocReplacedAnnotation);
            _statistics.ImplicitStackAllocCollected++;
        }
        else
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF040,
                $"pointer local '{declarator.Identifier.ValueText}' is registered as a lowerable " +
                "stackalloc local but its initializer is no longer a stackalloc expression " +
                "(internal rule gap).",
                declarator);
            return node;
        }

        if (node.Parent is not BlockSyntax parentBlock)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF040,
                $"stackalloc pointer local '{declarator.Identifier.ValueText}' must be declared directly " +
                "in a block so its backing array can be materialized; the original file was preserved.",
                node);
            return node;
        }

        var arrayName = MakeGeneratedName(
            declarator.Identifier.ValueText, "array", node);

        if (!_blockInjections.TryGetValue(parentBlock, out var per))
        {
            per = new Dictionary<StatementSyntax, List<StatementSyntax>>();
            _blockInjections[parentBlock] = per;
        }

        if (!per.TryGetValue(node, out var list))
        {
            list = new List<StatementSyntax>();
            per[node] = list;
        }

        list.Add(ArrayLocal(arrayName, elementTypeSyntax, newArray, node));

        _infos[symbol] = new PtrInfo
        {
            ArrayName = arrayName,
            SourceSlot = arrayName,
        };

        var rebuiltDeclarator = declarator
            .WithInitializer(EqualsValueClause(ZeroLiteral()));
        return node.WithDeclaration(
            VariableDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)),
                SingletonSeparatedList(rebuiltDeclarator)));
    }

    // ---- lambda 捕获 ----

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        // ByRef<T*>.Create 访问器 lambda（getter/setter）：捕获的游标在配对
        // 约定下随 holder 降级为 int 索引捕获，不是指针逃逸，整子树跳过。
        if (IsHolderAccessorArgument(node)) return node;
        CheckLambdaCapture(node);
        return base.VisitSimpleLambdaExpression(node);
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        if (IsHolderAccessorArgument(node)) return node;
        CheckLambdaCapture(node);
        return base.VisitParenthesizedLambdaExpression(node);
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        if (IsHolderAccessorArgument(node)) return node;
        CheckLambdaCapture(node);
        return base.VisitAnonymousMethodExpression(node);
    }

    /// <summary>
    /// lambda 是否直接作为 <c>ByRef&lt;T*&gt;.Create(...)</c> 的参数（双访问器
    /// 形态）。显式泛型在旧模型可能落在 CandidateSymbols（WrongArity），故
    /// 同时按调用表达式的返回类型是否为指针 holder 判定。
    /// </summary>
    private bool IsHolderAccessorArgument(SyntaxNode lambda)
    {
        if (lambda.Parent is not ArgumentSyntax argument
            || argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        var original = _resolver.Resolve(invocation);
        if (original is InvocationExpressionSyntax originalInvocation)
        {
            var info = _model.GetSymbolInfo(originalInvocation);
            foreach (var method in EnumerateSymbols(info))
            {
                if (PointerMethodCatalog.IsHolderCreateSymbol(method))
                    return true;
            }
        }

        return TypeInfoFor(invocation).Type is INamedTypeSymbol invokedType
               && PointerMethodCatalog.IsByRefHolder(invokedType, out _);
    }

    private void CheckLambdaCapture(SyntaxNode node)
    {
        foreach (var identifier in node.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = SymbolInfoFor(identifier).Symbol;
            if (symbol != null && _infos.ContainsKey(symbol))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer symbol '{identifier.Identifier.ValueText}' originating from a fixed " +
                    "block is captured in a lambda/anonymous method (escape); the original file was preserved.",
                    identifier);
                return;
            }
        }
    }

    // ---- 指针对（数组 + 索引）解析 ----

    private bool IsTrackedResolved(ISymbol? symbol)
        => symbol != null
           && _infos.TryGetValue(symbol, out var tracked)
           && tracked.Resolved;

    private bool TryResolvePointer(
        ExpressionSyntax raw,
        out string arrayName,
        out ExpressionSyntax indexExpression,
        out bool readOnly)
    {
        var e = UnwrapParenthesized(raw);
        arrayName = string.Empty;
        indexExpression = null!;
        readOnly = false;

        if (e is IdentifierNameSyntax identifier)
        {
            var symbol = SymbolInfoFor(identifier).Symbol;
            if (symbol != null
                && _infos.TryGetValue(symbol, out var info)
                && info.Resolved
                && !info.ReinterpretUnicode)
            {
                arrayName = info.ArrayName;
                indexExpression = e;
                readOnly = info.ReadOnlyString;
                return true;
            }

            // R5 配对域：指针形参/shadow/形参别名已折叠为「数组 + 同名 int
            // 索引变量」，其标识符在当前树仍是旧指针文本，按 flow 解析。
            if (symbol != null
                && _flowModel.TryGet(symbol, out var foldsFlow)
                && foldsFlow is { IsProvable: true, FoldsToOffset: true })
            {
                arrayName = foldsFlow.ArrayExpressionText;
                indexExpression = ParseExpression(foldsFlow.OffsetText);
                return true;
            }

            return false;
        }

        if (e is BinaryExpressionSyntax binary
            && (binary.IsKind(SyntaxKind.AddExpression)
                || binary.IsKind(SyntaxKind.SubtractExpression)))
        {
            var leftType = TypeInfoFor(binary.Left).Type;
            var rightType = TypeInfoFor(binary.Right).Type;

            ExpressionSyntax pointerSide;
            if (leftType is IPointerTypeSymbol)
            {
                pointerSide = binary.Left;
            }
            else if (rightType is IPointerTypeSymbol && binary.IsKind(SyntaxKind.AddExpression))
            {
                pointerSide = binary.Right;
            }
            else
            {
                return false;
            }

            if (TryResolvePointer(pointerSide, out var nestedArray, out _, out var nestedReadOnly))
            {
                arrayName = nestedArray;
                indexExpression = e; // 原文即合法 int 算术
                readOnly = nestedReadOnly;
                return true;
            }

            return false;
        }

        if (e is ConditionalExpressionSyntax conditional
            && TryResolvePointer(conditional.WhenTrue, out var trueArray, out _, out var trueReadOnly)
            && TryResolvePointer(conditional.WhenFalse, out var falseArray, out _, out var falseReadOnly)
            && trueArray == falseArray)
        {
            arrayName = trueArray;
            indexExpression = e;
            readOnly = trueReadOnly || falseReadOnly;
            return true;
        }

        if (e is CheckedExpressionSyntax checkedExpression)
        {
            return TryResolvePointer(
                checkedExpression.Expression, out arrayName, out indexExpression, out readOnly);
        }

        if (e is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression } addr
            && addr.Operand is ElementAccessExpressionSyntax elementAccess
            && elementAccess.ArgumentList.Arguments.Count == 1)
        {
            var arg = elementAccess.ArgumentList.Arguments[0].Expression;

            if (TryResolvePointer(elementAccess.Expression,
                    out var receiverArray, out var receiverIndex, out var receiverReadOnly))
            {
                arrayName = receiverArray;
                indexExpression = BinaryExpression(
                    SyntaxKind.AddExpression,
                    ParenthesizedExpression(receiverIndex),
                    ParenthesizedExpression(arg));
                readOnly = receiverReadOnly;
                return true;
            }

            var directType = TypeInfoFor(elementAccess.Expression).Type;
            var directSymbol = GetIdentifierSymbol(elementAccess.Expression);
            if (directType is IArrayTypeSymbol
                || (directSymbol is IFieldSymbol directField
                    && _rewrittenFields.Contains(directField)))
            {
                arrayName = elementAccess.Expression.ToString();
                indexExpression = arg;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// fixed 帧的真源源槽：数组/偏移帧按初始式真源（局部/形参/字段符号）归槽；
    /// 别名帧继承跟踪源的槽；string/scalar/null 帧物化副本彼此独立（两次 fixed
    /// 同一 string 会生成两份拷贝），槽即伴生数组名，保守不合并。
    /// 配对方法内，数组/偏移帧与本方法某指针形参命名族同族（形参 <c>pDst</c>
    /// 对 <c>fixed(char* pDstBegin = bufChars)</c>）时，帧槽提升为该族槽——
    /// 入口契约保证入参指针指向本帧缓冲，形参索引与帧索引可比较/相减。
    /// </summary>
    private string ResolveFrameSlot(
        InitKind kind,
        string arrayName,
        object? payload,
        string cursorName)
    {
        switch (kind)
        {
            case InitKind.Array when payload is ExpressionSyntax source:
                return TryResolveFamilySlot(cursorName)
                    ?? (TryExpressionSourceSlot(source, out var arraySlot)
                        ? arraySlot
                        : arrayName);

            case InitKind.Offset when payload is OffsetPayload offset:
                return TryResolveFamilySlot(cursorName)
                    ?? (TryExpressionSourceSlot(offset.Receiver, out var offsetSlot)
                        ? offsetSlot
                        : arrayName);

            case InitKind.Alias when payload is AliasPayload alias:
                return CompanionSlot(alias.SourceArray);

            default:
                return arrayName;
        }
    }

    /// <summary>
    /// 配对方法内，fixed 游标名与某指针形参命名族同族时返回该族的源槽键
    /// （与 R5 为族内形参登记的槽一致）；非配对方法或无同族形参返回 null。
    /// 命名族是入口契约约定（见 <see cref="PointerMethodCatalog.CursorFamily"/>）：
    /// 仅当方法作者以 pDst/pDstBegin 同族命名表达「入参指针即指向本缓冲」时才
    /// 合并，跨族（pSrc 形参对 pDstBegin 帧）不合并。
    /// </summary>
    private string? TryResolveFamilySlot(string cursorName)
    {
        if (_pairedStack.Count == 0)
            return null;

        var family = PointerMethodCatalog.CursorFamily(cursorName);
        foreach (var parameter in _pairedStack[^1].Parameters)
        {
            if (parameter.Kind != PairedParamKind.PlainPointer || parameter.Symbol == null)
                continue;
            if (!string.Equals(
                    PointerMethodCatalog.CursorFamily(parameter.Symbol.Name),
                    family,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slotSymbol = parameter.SlotBaseSymbol ?? parameter.Symbol;
            return PointerMethodCatalog.SymbolSlot(slotSymbol!);
        }

        return null;
    }

    /// <summary>
    /// 伴生数组名（R3 已降级帧的 <c>_p_array</c>，或 R5 flow 的数组文本如
    /// 形参名 <c>p</c>）→ 其真源源槽。
    /// </summary>
    private string CompanionSlot(string companion)
    {
        foreach (var info in _infos.Values)
        {
            if (info.Resolved && info.ArrayName == companion)
                return info.SourceSlot;
        }

        foreach (var flow in _flowModel.EnumerateFlows())
        {
            if (flow.IsProvable && flow.ArrayExpressionText == companion)
                return flow.SourceSlot ?? companion;
        }

        return companion;
    }

    private bool TryExpressionSourceSlot(ExpressionSyntax expression, out string slot)
        => PointerMethodCatalog.TryGetExpressionSlot(
            expression, _model, _resolver, out slot);

    /// <summary>
    /// 溯源 <c>(T*)handle.AddrOfPinnedObject()</c>：handle 必须是 GCHandle 局部，
    /// 且使用点之前最近一次 <c>handle = GCHandle.Alloc(arr, GCHandleType.Pinned)
    /// </c>（含声明初值）固定的是与强转元素同类型的简单数组标识符 arr。
    /// 命中返回 arr 的名字（数组变量重绑定后按名动态生效）与零索引；非 Pinned、
    /// 非数组实参、元素类型不符或句柄不是局部一律 false（保守，保留 UF020）。
    /// </summary>
    private bool TryResolvePinnedArrayPointer(
        ExpressionSyntax raw,
        SyntaxNode site,
        out string arrayName,
        out ExpressionSyntax indexExpression,
        out string sourceSlot)
    {
        arrayName = string.Empty;
        indexExpression = null!;
        sourceSlot = string.Empty;

        var e = UnwrapParenthesized(raw);
        if (e is not CastExpressionSyntax cast
            || TypeInfoFor(cast).Type is not IPointerTypeSymbol pointerType)
        {
            return false;
        }

        if (cast.Expression is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.ValueText != "AddrOfPinnedObject")
        {
            return false;
        }

        if (SymbolInfoFor(memberAccess).Symbol is not IMethodSymbol handleMethod
            || handleMethod.ContainingType.ToDisplayString()
                != "System.Runtime.InteropServices.GCHandle")
        {
            return false;
        }

        if (SymbolInfoFor(memberAccess.Expression).Symbol is not ILocalSymbol handleLocal)
            return false;

        var handleDeclarator = handleLocal.DeclaringSyntaxReferences
            .FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
        if (handleDeclarator == null) return false;

        // 候选 Alloc 站点：声明初值 + 句柄的全部赋值，取使用点之前最近一个。
        InvocationExpressionSyntax? nearestAlloc = null;
        var sitePosition = site.SpanStart;
        if (handleDeclarator.Initializer?.Value is { } declarationInit
            && UnwrapParenthesized(declarationInit) is InvocationExpressionSyntax declarationAlloc)
        {
            nearestAlloc = declarationAlloc;
        }

        var enclosingBody = site.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
                            ?? (SyntaxNode?)site.FirstAncestorOrSelf<LocalFunctionStatementSyntax>();
        if (enclosingBody != null)
        {
            foreach (var assignment in enclosingBody.DescendantNodes()
                         .OfType<AssignmentExpressionSyntax>()
                         .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                             && a.SpanStart <= sitePosition
                             && a.Left is IdentifierNameSyntax assignedIdentifier
                             && assignedIdentifier.Identifier.ValueText == handleLocal.Name))
            {
                if (SymbolInfoFor(assignment.Left).Symbol is not ILocalSymbol assignedHandle
                    || !SymbolEqualityComparer.Default.Equals(assignedHandle, handleLocal))
                {
                    continue;
                }

                if (UnwrapParenthesized(assignment.Right) is InvocationExpressionSyntax reassignedAlloc
                    && (nearestAlloc == null
                        || reassignedAlloc.SpanStart >= nearestAlloc.SpanStart))
                {
                    nearestAlloc = reassignedAlloc;
                }
            }
        }

        if (nearestAlloc == null) return false;

        if (nearestAlloc.Expression is not MemberAccessExpressionSyntax allocAccess
            || SymbolInfoFor(allocAccess).Symbol is not IMethodSymbol allocMethod
            || allocMethod.ContainingType.ToDisplayString()
                != "System.Runtime.InteropServices.GCHandle"
            || allocMethod.Name != "Alloc")
        {
            return false;
        }

        var arguments = nearestAlloc.ArgumentList.Arguments;
        if (arguments.Count < 2) return false;

        if (SymbolInfoFor(UnwrapParenthesized(arguments[1].Expression)).Symbol
            is not IFieldSymbol pinnedKind
            || pinnedKind.ContainingType?.Name != "GCHandleType"
            || pinnedKind.Name != "Pinned")
        {
            return false;
        }

        var arrayArg = UnwrapParenthesized(arguments[0].Expression);
        if (arrayArg is not IdentifierNameSyntax arrayIdentifier
            || TypeInfoFor(arrayArg).Type is not IArrayTypeSymbol arrayType)
        {
            return false;
        }

        if (!SymbolEqualityComparer.Default.Equals(
                arrayType.ElementType, pointerType.PointedAtType))
        {
            return false;
        }

        arrayName = arrayIdentifier.Identifier.ValueText;
        indexExpression = ZeroLiteral();
        // 源槽归 Alloc 固定的数组局部：同一 Pinned 数组的多个游标同源可比。
        sourceSlot = SymbolInfoFor(arrayIdentifier).Symbol is { } pinnedSymbol
            ? PointerMethodCatalog.SymbolSlot(pinnedSymbol)
            : arrayName;
        return true;
    }

    /// <summary>
    /// 溯源资源流指针局部初值 <c>byte* p = stream.PositionPointer</c>：元素必须
    /// 为 byte，stream 是初值为 <c>Assembly.GetManifestResourceStream(...)</c>
    /// 的局部（与 R5 字段写 RHS 资格同口径），且声明点之前没有对 stream 重
    /// 定位（Position 写/Seek）——资源指针从当前位置起读，重定位会使「读空整个
    /// 流」的归一改变语义。命中输出 stream 局部名与生成的伴生数组名。
    /// </summary>
    private bool TryResolveResourceStreamPointer(
        ExpressionSyntax raw,
        ILocalSymbol? pointerLocal,
        out string streamLocalName,
        out string? arrayName)
    {
        streamLocalName = string.Empty;
        arrayName = null;

        if (pointerLocal?.Type is not IPointerTypeSymbol localPointer
            || localPointer.PointedAtType.SpecialType != SpecialType.System_Byte)
        {
            return false;
        }

        if (!PointerMethodCatalog.IsManifestResourcePositionPointer(
                raw, _model, out streamLocalName, _resolver))
            return false;

        var originalDeclarator = pointerLocal.DeclaringSyntaxReferences
            .FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
        if (originalDeclarator == null) return false;

        var enclosingBody = originalDeclarator.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
                            ?? (SyntaxNode?)originalDeclarator.FirstAncestorOrSelf<LocalFunctionStatementSyntax>();
        if (enclosingBody != null)
        {
            var sitePosition = originalDeclarator.SpanStart;
            foreach (var memberAccess in enclosingBody.DescendantNodes()
                         .OfType<MemberAccessExpressionSyntax>()
                         .Where(m => m.SpanStart < sitePosition
                             && m.Name.Identifier.ValueText is "Position" or "Seek"))
            {
                if (memberAccess.Expression is not IdentifierNameSyntax receiver
                    || receiver.Identifier.ValueText != streamLocalName
                    || SymbolInfoFor(receiver).Symbol is not ILocalSymbol)
                {
                    continue;
                }

                var isPositionWrite = memberAccess.Name.Identifier.ValueText == "Position"
                    && memberAccess.Parent is AssignmentExpressionSyntax positionAssignment
                    && positionAssignment.Left == memberAccess;
                var isSeekCall = memberAccess.Name.Identifier.ValueText == "Seek"
                    && memberAccess.Parent is InvocationExpressionSyntax;
                if (isPositionWrite || isSeekCall)
                    return false;
            }
        }

        arrayName = MakeGeneratedName(pointerLocal.Name, "array", originalDeclarator);
        return true;
    }

    /// <summary>
    /// 为资源指针局部登记伴生语句（插在外层块中锚点语句之前）：
    /// <code>
    /// byte[] _p_array = new byte[(int)stream.Length];
    /// stream.Read(_p_array, 0, _p_array.Length);
    /// </code>
    /// 两语句与降级后的 <c>int p = 0;</c> 同作用域。声明语句不直接位于块
    /// 语句列表（如 switch 节）时保守拒绝。
    /// </summary>
    private bool TryRegisterResourceArrayInjection(
        LocalDeclarationStatementSyntax anchor,
        TypeSyntax elementType,
        string arrayName,
        string streamLocalName)
    {
        if (anchor.Parent is not BlockSyntax parentBlock)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"resource stream pointer local '{anchor.Declaration}' must be declared directly " +
                "in a block so its backing array can be materialized; the original file was preserved.",
                anchor);
            return false;
        }

        var streamLength = MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            IdentifierName(streamLocalName),
            IdentifierName("Length"));
        var newArray = ArrayCreationExpression(
            ArrayType(elementType)
                .WithRankSpecifiers(SingletonList(ArrayRankSpecifier(
                    SingletonSeparatedList<ExpressionSyntax>(
                        CastExpression(PredefinedType(Token(SyntaxKind.IntKeyword)), streamLength))))));

        var arrayLocal = ArrayLocal(arrayName, elementType, newArray, anchor);

        var readCall = InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName(streamLocalName),
                IdentifierName("Read")))
            .WithArgumentList(ArgumentList(SeparatedList(new[]
            {
                Argument(IdentifierName(arrayName)),
                Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))),
                Argument(MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName(arrayName),
                    IdentifierName("Length"))),
            })));
        var readStatement = ExpressionStatement(readCall)
            .WithLeadingTrivia(anchor.GetLeadingTrivia());

        if (!_blockInjections.TryGetValue(parentBlock, out var per))
        {
            per = new Dictionary<StatementSyntax, List<StatementSyntax>>();
            _blockInjections[parentBlock] = per;
        }

        if (!per.TryGetValue(anchor, out var list))
        {
            list = new List<StatementSyntax>();
            per[anchor] = list;
        }

        list.Add(arrayLocal);
        list.Add(readStatement);
        return true;
    }

    // ---- byte*→char* UTF-16 重解释局部（new String 实参专用） ----

    /// <summary>
    /// 登记 fixed 域内 <c>char* p = (char*)(pb + byteOffset)</c> 重解释局部：
    /// 源指针必须可溯源到 byte 数组索引，且 fixed 块内该局部（声明之后）的
    /// <b>全部</b>使用只出现在 <c>new String(p[, +charOffset], 0, cch)</c> 的
    /// 首参数位置（不被 lambda/局部函数捕获、不自增/赋值/取址/下标）。命中后
    /// 声明由调用方移除，使用点由
    /// <see cref="TryBuildUnicodeGetString"/> 归一为 Encoding.Unicode.GetString。
    /// 任何结构性前提不满足均返回 false（保守，落既有 UF020 路径）。
    /// </summary>
    private bool TryRegisterUnicodeReinterpret(
        ExpressionSyntax initializer,
        ILocalSymbol local,
        LocalDeclarationStatementSyntax declaration,
        VariableDeclaratorSyntax declarator)
    {
        if (local.Type is not IPointerTypeSymbol { PointedAtType.SpecialType: SpecialType.System_Char })
            return false;

        if (initializer is not CastExpressionSyntax cast)
            return false;

        var inner = UnwrapParenthesized(cast.Expression);
        if (TypeInfoFor(inner).Type is not IPointerTypeSymbol sourcePointer
            || sourcePointer.PointedAtType.SpecialType != SpecialType.System_Byte)
        {
            return false;
        }

        if (!TryResolvePointer(inner, out var byteArray, out var byteIndex, out _))
            return false;

        var fixedStatement = declaration.FirstAncestorOrSelf<FixedStatementSyntax>();
        if (fixedStatement == null)
            return false;

        var uses = fixedStatement.Statement.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => id.SpanStart > declarator.Span.End
                && SymbolEqualityComparer.Default.Equals(SymbolInfoFor(id).Symbol, local))
            .ToList();
        if (uses.Count == 0)
            return false;

        foreach (var use in uses)
        {
            if (!IsReinterpretStringCtorArgument(use, fixedStatement))
                return false;
        }

        _infos[local] = new PtrInfo
        {
            ArrayName = byteArray,
            SourceSlot = byteArray,
            Resolved = true,
            ReinterpretUnicode = true,
            ByteIndexText = "(" + byteIndex.ToString() + ")",
        };
        return true;
    }

    /// <summary>
    /// use 是否为 <c>new String(p[, +charOffset], 0, cch)</c> 的首参数（首参数
    /// 即 use 自身或 use + 整型偏移），且中途未被 lambda/局部函数捕获。
    /// </summary>
    private bool IsReinterpretStringCtorArgument(
        IdentifierNameSyntax use,
        FixedStatementSyntax fixedStatement)
    {
        if (use.Ancestors()
            .TakeWhile(a => a != fixedStatement.Statement)
            .Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
        {
            return false;
        }

        ExpressionSyntax access = use;
        SyntaxNode? accessParent = use.Parent;
        if (accessParent is BinaryExpressionSyntax add
            && add.IsKind(SyntaxKind.AddExpression)
            && add.Left == use
            && IsIntegralType(TypeInfoFor(add.Right).Type))
        {
            access = add;
            accessParent = add.Parent;
        }

        while (accessParent is ParenthesizedExpressionSyntax parenthesized)
            accessParent = parenthesized.Parent;

        if (accessParent is not ArgumentSyntax argument
            || argument.RefKindKeyword.Kind() != SyntaxKind.None
            || argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Parent is not ObjectCreationExpressionSyntax creation
            || !ReferenceEquals(UnwrapParenthesized(argument.Expression), access)
            || argumentList.Arguments.Count != 3
            || !ReferenceEquals(argument, argumentList.Arguments[0])
            || !IsZeroLiteralExpression(
                UnwrapParenthesized(argumentList.Arguments[1].Expression)))
        {
            return false;
        }

        return TypeInfoFor(creation).Type?.SpecialType == SpecialType.System_String;
    }

    /// <summary>
    /// <c>new String(p[, +charOffset], 0, cch)</c>（p 为 byte*→char* 重解释
    /// 局部）→ <c>Encoding.Unicode.GetString(bytes, byteIndex, checked(cch*2))</c>。
    /// </summary>
    private bool TryBuildUnicodeGetString(
        ObjectCreationExpressionSyntax creation,
        out InvocationExpressionSyntax invocation)
    {
        invocation = null!;

        if (TypeInfoFor(creation).Type?.SpecialType != SpecialType.System_String
            || creation.ArgumentList is not { Arguments.Count: 3 } arguments
            || !IsZeroLiteralExpression(UnwrapParenthesized(arguments.Arguments[1].Expression)))
        {
            return false;
        }

        var firstArgument = UnwrapParenthesized(arguments.Arguments[0].Expression);
        ExpressionSyntax? charOffset = null;
        IdentifierNameSyntax? pointerIdentifier = firstArgument as IdentifierNameSyntax;
        if (pointerIdentifier == null
            && firstArgument is BinaryExpressionSyntax offsetAdd
            && offsetAdd.IsKind(SyntaxKind.AddExpression)
            && offsetAdd.Left is IdentifierNameSyntax offsetIdentifier)
        {
            pointerIdentifier = offsetIdentifier;
            charOffset = offsetAdd.Right;
            if (!IsIntegralType(TypeInfoFor(charOffset).Type))
                return false;
        }

        if (pointerIdentifier == null
            || SymbolInfoFor(pointerIdentifier).Symbol is not ILocalSymbol local
            || !_infos.TryGetValue(local, out var info)
            || !info.ReinterpretUnicode)
        {
            return false;
        }

        var byteIndexText = info.ByteIndexText!;
        if (charOffset != null)
            byteIndexText += " + (" + charOffset + ") * 2";

        var charCount = UnwrapParenthesized(arguments.Arguments[2].Expression);
        var byteCount = CheckedExpression(
            SyntaxKind.CheckedExpression,
            ParseExpression("(" + charCount + ") * 2"));

        invocation = InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    ParseExpression("global::System.Text.Encoding"),
                    IdentifierName("Unicode")),
                IdentifierName("GetString")))
            .WithArgumentList(ArgumentList(SeparatedList(new[]
            {
                Argument(IdentifierName(info.ArrayName)),
                Argument(ParseExpression(byteIndexText)),
                Argument(byteCount),
            })));
        return true;
    }

    private static bool IsIntegralType(ITypeSymbol? type)
        => type?.SpecialType is SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Byte
            or SpecialType.System_SByte
            or SpecialType.System_Char;

    // ---- 构造辅助 ----

    private LocalDeclarationStatementSyntax ArrayLocal(
        string name, TypeSyntax elementType, ExpressionSyntax initializer, SyntaxNode anchor)
    {
        var arrayType = ArrayType(elementType)
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier()));
        var statement = LocalDeclarationStatement(VariableDeclaration(arrayType,
            SingletonSeparatedList(VariableDeclarator(name)
                .WithInitializer(EqualsValueClause(initializer)))));
        return statement.WithLeadingTrivia(anchor.GetLeadingTrivia());
    }

    private LocalDeclarationStatementSyntax IndexLocal(
        string name, ExpressionSyntax initializer, SyntaxTriviaList lineStart)
    {
        return LocalDeclarationStatement(VariableDeclaration(
            PredefinedType(Token(SyntaxKind.IntKeyword)),
            SingletonSeparatedList(VariableDeclarator(name)
                .WithInitializer(EqualsValueClause(initializer)))))
            .WithLeadingTrivia(lineStart);
    }

    /// <summary>
    /// 登记「在原声明语句前注入伴生语句」，由 <see cref="VisitBlock"/> 按锚点
    /// 插入同一块语句列表（保持作用域，配对/降级局部的伴生数组与 int 索引
    /// 必须与后续使用同层可见）。
    /// </summary>
    private void InjectBeforeStatement(
        BlockSyntax parentBlock, StatementSyntax anchor, StatementSyntax statement)
    {
        if (!_blockInjections.TryGetValue(parentBlock, out var perAnchor))
        {
            perAnchor = new Dictionary<StatementSyntax, List<StatementSyntax>>();
            _blockInjections[parentBlock] = perAnchor;
        }

        if (!perAnchor.TryGetValue(anchor, out var list))
        {
            list = new List<StatementSyntax>();
            perAnchor[anchor] = list;
        }

        list.Add(statement);
    }

    private static LiteralExpressionSyntax ZeroLiteral() =>
        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0));

    private static ExpressionSyntax UnwrapParenthesized(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private ISymbol? GetIdentifierSymbol(ExpressionSyntax expression)
    {
        expression = UnwrapParenthesized(expression);
        return expression switch
        {
            IdentifierNameSyntax id => SymbolInfoFor(id).Symbol,
            // f.buf 这类成员访问同样取其字段符号（R2 字段可能经实例限定）。
            MemberAccessExpressionSyntax memberAccess => SymbolInfoFor(memberAccess).Symbol,
            _ => null,
        };
    }

    // 语义查询：节点属于原始模型树（未变更节点或
    // DeclaringSyntaxReferences 取出的原始声明节点）直接查模型；重建红包装
    // 节点先经 resolver 映射。
    private TypeInfo TypeInfoFor(ExpressionSyntax expression)
    {
        expression = UnwrapParenthesized(expression);
        if (expression.SyntaxTree == _model.SyntaxTree)
            return _model.GetTypeInfo(expression);

        return _resolver.Resolve(expression) is { } originalTypeNode
            ? _model.GetTypeInfo(originalTypeNode)
            : default;
    }

    private SymbolInfo SymbolInfoFor(ExpressionSyntax expression)
    {
        expression = UnwrapParenthesized(expression);
        if (expression.SyntaxTree == _model.SyntaxTree)
            return _model.GetSymbolInfo(expression);

        return _resolver.Resolve(expression) is { } originalSymbolNode
            ? _model.GetSymbolInfo(originalSymbolNode)
            : default;
    }

    private static bool IsWriteTarget(ExpressionSyntax node)
    {
        var parent = node.Parent;
        if (parent is AssignmentExpressionSyntax assignment && assignment.Left == node) return true;
        if (parent is PostfixUnaryExpressionSyntax) return true;
        if (parent is PrefixUnaryExpressionSyntax prefix
            && (prefix.Kind() == SyntaxKind.PreIncrementExpression
                || prefix.Kind() == SyntaxKind.PreDecrementExpression))
        {
            return true;
        }
        if (parent is ArgumentSyntax argument
            && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            return true;
        }
        return false;
    }

    private SyntaxTriviaList LineStartTrivia(SyntaxNode node)
    {
        var indent = string.Empty;
        var leading = node.GetLeadingTrivia();
        if (leading.Count > 0)
        {
            var last = leading[^1];
            if (last.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                indent = last.ToString();
            }
        }

        return TriviaList(EndOfLine(System.Environment.NewLine), Whitespace(indent));
    }

    private string MakeGeneratedName(string pointerName, string suffix, SyntaxNode node)
    {
        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() ?? node;
        if (!_scopeNameCache.TryGetValue(member, out var names))
        {
            names = new HashSet<string>(
                member.DescendantTokens().Select(t => t.ValueText));
            _scopeNameCache[member] = names;
        }

        var candidate = "_" + pointerName + "_" + suffix;
        var counter = 2;
        while (names.Contains(candidate) || _generatedNames.Contains(candidate))
        {
            candidate = "_" + pointerName + "_" + suffix + "_" + counter;
            counter++;
        }

        names.Add(candidate);
        _generatedNames.Add(candidate);
        return candidate;
    }

    private void CountKind(InitKind kind)
    {
        switch (kind)
        {
            case InitKind.Array: _statistics.FixedVariablesArray++; break;
            case InitKind.Offset: _statistics.FixedVariablesOffset++; break;
            case InitKind.Alias: _statistics.FixedVariablesAlias++; break;
            case InitKind.Null: _statistics.FixedVariablesNull++; break;
            case InitKind.String: _statistics.FixedVariablesString++; break;
            case InitKind.Scalar: _statistics.FixedVariablesScalar++; break;
        }
    }

    private void AddError(UnsafeFfmDiagnosticCode code, string message, SyntaxNode node)
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
            case UnsafeFfmDiagnosticCode.UF030: _statistics.Uf030Rejections++; break;
        }
    }
}
