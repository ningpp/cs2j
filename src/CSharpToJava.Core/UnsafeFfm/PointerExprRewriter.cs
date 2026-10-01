using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R6：指针表达式索引化改写（设计 D7，§4 组件表；M3a 基础族）。
/// 本部分只消费两类可证源——R5 <see cref="PointerSignatureRewriter"/> 改写的
/// 指针形参/局部（参数源）与 R2 改写的数组字段经 R5 注册的局部（局部数组源），
/// 全部基于 <see cref="PointerFlowModel"/>，不做文本匹配：
/// <list type="bullet">
/// <item><c>&amp;arr[i]</c>/<c>&amp;arr[0]</c> 取址模式按「数组引用 + 索引」消解
/// （R6 阶段仍游离的取址即逃逸 → UF020）；</item>
/// <item><c>*p</c>→<c>arr[offset]</c>；<c>p[i]</c>→<c>arr[offset+i]</c>；
/// 数组表达式与偏移按 flow 文本在当前树重建（R3/R4 已重建过树，旧节点已脱离）；</item>
/// <item>struct 指针 <c>p-&gt;m</c> → UF020；</item>
/// <item>指针↔整数/异元素指针强转 <c>(int*)p</c>/<c>(int)p</c>：本部分
/// 不可证 → UF021（「同数组内差转 int」属 M3b）；</item>
/// <item>算术（p±n、p-q）、比较（&lt;&gt;&lt;=&gt;=、==!=）、++--：M3b 族，
/// 本部分模型不能处理 → UF022 显式失败，禁止原样输出错误代码；</item>
/// <item>R3 已降级符号（fixed 变量/被转换局部，现为 int 索引）的使用点按 int 识别，
/// 不误报。</item>
/// </list>
/// 产物不含指针表达式；输入树无指针时本规则不产生任何变化，天然幂等。
/// </summary>
internal sealed class PointerExprRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly PointerFlowModel _flowModel;
    private readonly IReadOnlyCollection<ISymbol> _loweredSymbols;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;

    public PointerExprRewriter(
        SemanticModel model,
        PointerFlowModel flowModel,
        IReadOnlyCollection<ISymbol> loweredSymbols,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver)
    {
        _model = model;
        _flowModel = flowModel;
        _loweredSymbols = loweredSymbols;
        _statistics = statistics;
        _resolver = resolver;
    }

    public List<UnsafeFfmDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    // ---- *p / &x ----

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Kind() == SyntaxKind.PointerIndirectionExpression)
        {
            var operand = UnwrapParenthesized(node.Operand);

            if (TryGetFlow(operand, out var flow))
            {
                if (!flow.IsProvable)
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF020,
                        $"pointer expression '{node}' is dereferenced through a symbol whose source is " +
                        "not provable; the original file was preserved.",
                        node);
                    return node;
                }

                var array = ParseExpression(flow.ArrayExpressionText);
                var index = ParseExpression(flow.OffsetText);
                var result = ElementAccessExpression(array)
                    .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                        Argument(index))));
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
            return HandlePointerMutation(node, node.Operand);
        }

        return base.VisitPrefixUnaryExpression(node);
    }

    // ---- p++/p-- ----

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.Kind() is SyntaxKind.PostIncrementExpression
            or SyntaxKind.PostDecrementExpression)
        {
            return HandlePointerMutation(node, node.Operand);
        }

        return base.VisitPostfixUnaryExpression(node);
    }

    private SyntaxNode HandlePointerMutation(SyntaxNode node, ExpressionSyntax operand)
    {
        var target = UnwrapParenthesized(operand);
        if (IsLoweredExpression(target)) return node;

        if (IsOldPointerTyped(target) || TryGetFlow(target, out _))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF022,
                $"pointer increment/decrement '{node}' belongs to the pointer arithmetic family " +
                "that is deferred to M3b; the current model cannot fold the offset back, and the " +
                "expression is never passed through unchanged.",
                node);
            return node;
        }

        return node is PrefixUnaryExpressionSyntax prefix
            ? base.VisitPrefixUnaryExpression(prefix)
            : base.VisitPostfixUnaryExpression((PostfixUnaryExpressionSyntax)node);
    }

    // ---- p[i] ----

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        var receiver = UnwrapParenthesized(node.Expression);

        if (TryGetFlow(receiver, out var flow))
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
            ExpressionSyntax index;
            if (flow.OffsetText == "0")
            {
                index = arg;
            }
            else
            {
                index = BinaryExpression(
                    SyntaxKind.AddExpression,
                    ParenthesizedExpression(ParseExpression(flow.OffsetText)),
                    ParenthesizedExpression(arg));
            }

            var result = ElementAccessExpression(ParseExpression(flow.ArrayExpressionText))
                .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                    Argument(index))));

            // 偏移 0 且数组名即接收者名时输出与原节点同构：无需计数，返回原节点。
            if (flow.OffsetText == "0"
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
        var operandIsOldPointer = IsOldPointerTyped(operand);

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

        if (operandIsOldPointer)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF021,
                $"cast '{node}' converts a pointer to a non-pointer or to a different pointer type; " +
                "only a same-array pointer difference to int (M3b) will be supported, and the cast " +
                "is never passed through unchanged.",
                node);
            return node;
        }

        return base.VisitCastExpression(node);
    }

    // ---- 算术 / 比较 ----

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (IsPointerBinary(node))
        {
            // 整个二元做 lowered 判定：无符号来源的操作数（整型字面量等，
            // 如 lowered fixed 块内的 p + 1）不引入未降级符号，只要全部
            // 指针来源符号都在 R3 降级域即放行；逐侧要求 IsLoweredExpression
            // 会让字面量一侧恒为 false，误拒 M3a 支持的 p±n。
            if (IsLoweredExpression(node))
                return base.VisitBinaryExpression(node);

            AddError(
                UnsafeFfmDiagnosticCode.UF022,
                $"pointer arithmetic/comparison expression '{node.ToString().Replace("\n", " ")}' " +
                "belongs to the expression family deferred to M3b (p±n, p-q, pointer comparisons); " +
                "the current model cannot lower it, and it is never emitted unchanged.",
                node);
            return node;
        }

        return base.VisitBinaryExpression(node);
    }

    /// <summary>
    /// 是否指针二元：任一侧旧类型为指针（含 flow 符号——其旧声明类型即指针），
    /// 运算为加减或比较/相等。
    /// </summary>
    private bool IsPointerBinary(BinaryExpressionSyntax node)
    {
        if (node.Kind() is not (SyntaxKind.AddExpression
            or SyntaxKind.SubtractExpression
            or SyntaxKind.LessThanExpression
            or SyntaxKind.GreaterThanExpression
            or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanOrEqualExpression
            or SyntaxKind.EqualsExpression
            or SyntaxKind.NotEqualsExpression))
        {
            return false;
        }

        var left = UnwrapParenthesized(node.Left);
        var right = UnwrapParenthesized(node.Right);

        if (IsOldPointerTyped(left) || IsOldPointerTyped(right)) return true;
        return TryGetFlow(left, out _) || TryGetFlow(right, out _);
    }

    // ---- 赋值 / 实参 / 返回 ----

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var right = UnwrapParenthesized(node.Right);

        // 左值为 *p/p[i] 时由前缀/下标访问的 Visit 归一为数组元素，此处照常访问。
        var rightIsFlow = TryGetFlow(right, out var rightFlow);
        var rightIsOldPointer = IsOldPointerTyped(right);

        if (!IsLoweredExpression(right) && (rightIsOldPointer || rightIsFlow))
        {
            if (rightIsFlow && rightFlow.IsProvable)
            {
                // 数组引用赋值（含 q = p）：正常托管赋值，保留。
                return base.VisitAssignmentExpression(node);
            }

            if (right.IsKind(SyntaxKind.NullLiteralExpression))
                return base.VisitAssignmentExpression(node);

            var code = right is BinaryExpressionSyntax
                ? UnsafeFfmDiagnosticCode.UF022
                : UnsafeFfmDiagnosticCode.UF020;
            AddError(
                code,
                $"assignment '{node.ToString().Replace("\n", " ")}' assigns a pointer value whose " +
                "source is not provable in the current model; the original file was preserved.",
                node);
            return node;
        }

        return base.VisitAssignmentExpression(node);
    }

    public override SyntaxNode? VisitArgument(ArgumentSyntax node)
    {
        var expression = UnwrapParenthesized(node.Expression);

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
