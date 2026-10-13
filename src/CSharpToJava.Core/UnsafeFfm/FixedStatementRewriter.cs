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

    private enum InitKind { Array, Offset, Alias, Null, String, Scalar, Error }

    private sealed record OffsetPayload(ExpressionSyntax Receiver, ExpressionSyntax Index);
    private sealed record AliasPayload(string SourceArray, ExpressionSyntax Index, bool ReadOnly);

    private sealed class PtrInfo
    {
        public required string ArrayName { get; set; }
        public bool ReadOnlyString { get; init; }
        public bool Resolved { get; set; } = true;
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

    private readonly Dictionary<ISymbol, PtrInfo> _infos = new();
    private readonly HashSet<string> _generatedNames = new();
    private readonly Dictionary<SyntaxNode, HashSet<string>> _scopeNameCache = new();

    /// <summary>当前访问子树所属的配对方法计划栈（返回/赋值逃逸门放行用）。</summary>
    private readonly List<PairedMethodPlan> _pairedStack = new();

    public FixedStatementRewriter(
        SemanticModel model,
        HashSet<IFieldSymbol> rewrittenFields,
        HashSet<ISymbol> pointerRewritten,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver,
        PointerMethodCatalog catalog,
        PointerFlowModel flowModel)
    {
        _model = model;
        _rewrittenFields = rewrittenFields;
        _pointerRewritten = pointerRewritten;
        _statistics = statistics;
        _resolver = resolver;
        _catalog = catalog;
        _flowModel = flowModel;
    }

    /// <summary>
    /// R3 已降级指针符号 → 其数组名/只读标记（解析成功的 fixed/别名局部）。
    /// 供 R6 在跨域表达式（配对形参与 fixed 索引混算）中把这些 int 索引
    /// 重新还原为「数组 + 索引」对。
    /// </summary>
    public IReadOnlyDictionary<ISymbol, (string ArrayName, bool ReadOnly)> LoweredArrayMap
    {
        get
        {
            var map = new Dictionary<ISymbol, (string, bool)>(
                SymbolEqualityComparer.Default);
            foreach (var (symbol, info) in _infos)
            {
                if (info.Resolved && info.ArrayName.Length > 0)
                    map[symbol] = (info.ArrayName, info.ReadOnlyString);
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
                ReadOnlyString = record.Kind == InitKind.String,
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
                    statements.Add(ArrayLocal(record.ArrayName, charType,
                        InvocationExpression(MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            sourceExpr,
                            IdentifierName("ToCharArray")))
                        .WithArgumentList(ArgumentList()), node));
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
                        Argument(index))));
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
                        Argument(index))));
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
                }
                else if (TryResolvePointer(rhs, out var sourceArray, out var indexExpression, out _))
                {
                    newRhs = indexExpression;
                    info.ArrayName = sourceArray;
                    info.Resolved = true;
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

    // ---- 从 fixed 指针重建 Span：逃逸，UF022 ----

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
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
            ExpressionSyntax? newInitializer = null;

            if (rawInitializer != null)
            {
                var initializer = UnwrapParenthesized(rawInitializer);

                if (initializer.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    arrayName = MakeGeneratedName(declarator.Identifier.ValueText, "array", node);
                    extraStatements.Add(ArrayLocal(arrayName, elementTypeSyntax,
                        PostfixUnaryExpression(
                            SyntaxKind.SuppressNullableWarningExpression,
                            LiteralExpression(SyntaxKind.NullLiteralExpression)),
                        node));
                    newInitializer = ZeroLiteral();
                }
                else if (TryResolvePointer(initializer,
                             out var sourceArray, out var indexExpression, out _))
                {
                    arrayName = sourceArray;
                    newInitializer = indexExpression;
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
                Resolved = arrayName != null,
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
                && info.Resolved)
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
