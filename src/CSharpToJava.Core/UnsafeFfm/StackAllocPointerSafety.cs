using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// UF040 根因判定（裸指针 stackalloc 局部）：站点能否统一降级为
/// 「堆数组伴生局部 <c>T[] _p_array = new T[n];</c> + 同名 int 索引局部
/// <c>int p = 0;</c>」（复用 R3 <see cref="FixedStatementRewriter"/> 的
/// PtrInfo/_infos 指针局部→索引机制与 R6 paired-index 展开）。
/// <para>
/// 可降级要求（全部基于原始树 <see cref="SemanticModel"/>，无文件名/方法名
/// 白名单）：
/// <list type="bullet">
/// <item>站点是块语句列表中的指针局部（含 <c>var</c>，自然类型 T*）声明
/// 初值，声明直接位于某方法/局部函数体内（lambda/匿名方法/访问器不受理）；</item>
/// <item>元素类型通过 <see cref="StackAllocElementSafety.IsSafeElement"/>；</item>
/// <item>由该 stackalloc 种子经裸别名（<c>T* q = p;</c>）、偏移别名
/// （<c>q = p + n</c>）、<c>&amp;p[k]</c>/裸帧嵌套 fixed 与同局部重绑定
/// 闭包可达的全部游标，其使用只落在可索引化形态：下标读写、<c>*p</c>、
/// 指针 ± int、同源游标比较/相减、<c>++/--/+=/-=</c>、嵌套 fixed 初值、
/// 传给已配对（含被信号⑦强制配对）的源码指针方法或含白名单指针形参的
/// 元数据方法（如 <c>Encoding.UTF8.GetBytes</c>，R6 既有 int 索引实参
/// 通道）；</item>
/// <item>不发生外泄：从方法/局部函数返回、写入字段/属性、lambda/局部函数
/// 捕获、经非白名单指针签名方法/抽象虚方法/委托外发、holder 提升、Span
/// 重建、取址逃逸、指针强转、p-&gt;m 等。</item>
/// </list>
/// 判据保守：任何无法证明留在可索引域内的形态均返回 false（保持 UF040）。
/// </para>
/// </summary>
internal static class StackAllocPointerSafety
{
    /// <summary>
    /// 判定一个裸指针 stackalloc 声明站点是否可降级。<paramref name="catalog"/>
    /// 须已在调用前对站点所属方法调用过 <see cref="PointerMethodCatalog.GetPlan"/>
    /// （强制配对闭包已传播）。
    /// <para>
    /// <paramref name="staysAtZero"/> 在返回 true 时标识种子索引局部在整个作用域
    /// 内不发生任何自增/偏移重绑定（裸实参外发点索引恒为 0），供 R6 元数据
    /// 数组重载展开的容量参数消去使用。
    /// </para>
    /// </summary>
    public static bool IsLowerable(
        VariableDeclaratorSyntax seedDeclarator,
        SemanticModel model,
        PointerMethodCatalog catalog,
        out string reason,
        out bool staysAtZero)
    {
        reason = string.Empty;
        staysAtZero = false;

        var seedLocal = model.GetDeclaredSymbol(seedDeclarator) as ILocalSymbol;
        if (seedLocal?.Type is not IPointerTypeSymbol seedPointer)
        {
            reason = "the stackalloc target is not a pointer local.";
            return false;
        }

        if (seedDeclarator is
            {
                Parent: VariableDeclarationSyntax
                {
                    Variables.Count: 1,
                    Parent: LocalDeclarationStatementSyntax declaration,
                }
            }
            && TryGetStackAllocExpression(seedDeclarator, out _))
        {
            // 形态合法
        }
        else
        {
            reason = "the stackalloc is not the sole initializer of a pointer local declaration; " +
                     "multi-declarator pointer statements are not lowered.";
            return false;
        }

        if (declaration.Parent is not BlockSyntax)
        {
            reason = "the pointer local is not declared directly in a block statement list; its " +
                     "backing array cannot be materialized there.";
            return false;
        }

        var scope = declaration.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()
                    as SyntaxNode
                    ?? declaration.FirstAncestorOrSelf<LocalFunctionStatementSyntax>();
        if (scope is AccessorDeclarationSyntax)
        {
            reason = "the pointer local is declared inside a property/event accessor; its backing " +
                     "array cannot be materialized there.";
            return false;
        }

        if (scope == null
            || declaration.Ancestors().Any(a => a is AnonymousFunctionExpressionSyntax
                or AccessorDeclarationSyntax
                or LocalFunctionStatementSyntax))
        {
            reason = "the pointer local is declared inside a lambda, anonymous method, accessor or " +
                     "other unsupported scope.";
            return false;
        }

        var seedElement = seedPointer.PointedAtType;
        if (!StackAllocElementSafety.IsSafeElement(seedElement, out var elementReason))
        {
            reason = elementReason;
            return false;
        }

        var nestedRegions = scope.DescendantNodes().Where(IsNestedRegion).ToList();
        bool IsInsideNestedRegion(SyntaxNode node) =>
            nestedRegions.Any(n => n != node && n.Contains(node));

        if (IsInsideNestedRegion(seedDeclarator))
        {
            reason = "the pointer local is declared inside a nested local function or lambda.";
            return false;
        }

        var tracked = BuildTrackedClosure(scope, model, IsInsideNestedRegion);
        if (!tracked.TryGetValue(seedLocal, out var seedSlot) || seedSlot == null)
        {
            reason = "internal gap: seed local missing from provenance closure.";
            return false;
        }

        // 种子自身的初值/重绑定必须全部留在闭包可证域内（BuildTrackedClosure
        // 对不可证初值返回 null 槽）。
        if (tracked[seedLocal] == null)
        {
            reason = "the stackalloc pointer is reassigned from an expression whose array origin " +
                     "is not provable.";
            return false;
        }

        // 引用遍历（排除声明标识符位与嵌套作用域）。
        var seedMutated = false;
        foreach (var identifier in scope.DescendantNodes()
                     .OfType<IdentifierNameSyntax>())
        {
            if (IsInsideNestedRegion(identifier))
            {
                // 捕获判定仅针对 tracked 符号。
                if (model.GetSymbolInfo(identifier).Symbol is { } captured
                    && tracked.ContainsKey(captured))
                {
                    reason = $"pointer '{captured.Name}' originating from a stackalloc buffer is " +
                             "captured in a lambda or local function (escape).";
                    return false;
                }

                continue;
            }

            if (model.GetSymbolInfo(identifier).Symbol is not { } used || !tracked.ContainsKey(used))
                continue;
            if (IsDeclaratorIdentifierPosition(identifier)) continue;

            if (!ClassifyUse(identifier, model, catalog, tracked, scope, out var useReason))
            {
                reason = useReason;
                return false;
            }

            if (SymbolEqualityComparer.Default.Equals(used, seedLocal)
                && IsSeedMutationTarget(identifier))
            {
                seedMutated = true;
            }
        }

        staysAtZero = !seedMutated;
        return true;
    }

    /// <summary>
    /// 种子标识符自身是否为自增/自减/复合或简单赋值目标（<c>p++</c>、
    /// <c>p += n</c>、<c>p = q</c>；<c>p[i]++</c>/<c>*p = x</c> 不算游标自增）。
    /// </summary>
    private static bool IsSeedMutationTarget(IdentifierNameSyntax identifier)
    {
        var parent = identifier.Parent;
        while (parent is ParenthesizedExpressionSyntax)
            parent = parent.Parent;

        if (parent is PrefixUnaryExpressionSyntax prefix
            && (prefix.IsKind(PreIncrementExpression) || prefix.IsKind(PreDecrementExpression))
            && UnwrapParens(prefix.Operand) == identifier)
            return true;

        if (parent is PostfixUnaryExpressionSyntax post
            && (post.IsKind(PostIncrementExpression) || post.IsKind(PostDecrementExpression))
            && UnwrapParens(post.Operand) == identifier)
            return true;

        if (parent is AssignmentExpressionSyntax assignment
            && UnwrapParens(assignment.Left) == identifier)
            return true;

        return false;
    }

    /// <summary>剥除括号表达式（含 checked 表达式不处理，调用方仅需括号归一）。</summary>
    private static ExpressionSyntax UnwrapParens(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    /// <summary>节点是否为嵌套作用域（局部函数/lambda/匿名方法）。</summary>
    private static bool IsNestedRegion(SyntaxNode node)
        => node is LocalFunctionStatementSyntax
            or SimpleLambdaExpressionSyntax
            or ParenthesizedLambdaExpressionSyntax
            or AnonymousMethodExpressionSyntax;

    private static bool IsDeclaratorIdentifierPosition(IdentifierNameSyntax identifier)
    {
        // VariableDeclarator.Identifier 是 SyntaxToken，不会解析为
        // IdentifierNameSyntax；fixed/local 声明名位天然不会出现于引用遍历，
        // 该判定保留作语义防护。
        return false;
    }

    /// <summary>
    /// 构造作用域内指针局部/fixed 变量的可证闭包：键=游标符号，值=其唯一
    /// 真源源槽（stackalloc 种子以自身符号为槽；裸/偏移/取址别名继承根槽；
    /// 多源或不可证时值为 null——该符号留在字典中以便引用点给出精确拒绝）。
    /// </summary>
    private static Dictionary<ISymbol, object?> BuildTrackedClosure(
        SyntaxNode scope,
        SemanticModel model,
        Func<SyntaxNode, bool> isInsideNestedRegion)
    {
        var declarators = scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(d => !isInsideNestedRegion(d))
            .Where(d => d.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() != null
                || d.FirstAncestorOrSelf<FixedStatementSyntax>() != null)
            .ToList();

        var symbols = new List<(VariableDeclaratorSyntax Declarator, ILocalSymbol Local)>();
        foreach (var declarator in declarators)
        {
            if (model.GetDeclaredSymbol(declarator) is ILocalSymbol { Type: IPointerTypeSymbol } local)
                symbols.Add((declarator, local));
        }

        var enclosingBody = scope is BaseMethodDeclarationSyntax method
            ? method.Body
            : ((LocalFunctionStatementSyntax)scope).Body;

        var assignments = new Dictionary<ISymbol, List<ExpressionSyntax>>(
            SymbolEqualityComparer.Default);
        if (enclosingBody != null)
        {
            foreach (var assignment in enclosingBody.DescendantNodes()
                         .OfType<AssignmentExpressionSyntax>()
                         .Where(a => a.IsKind(SimpleAssignmentExpression)
                             && !isInsideNestedRegion(a)))
            {
                var left = model.GetSymbolInfo(assignment.Left).Symbol;
                if (left == null) continue;

                if (assignments.TryGetValue(left, out var list))
                    list.Add(assignment.Right);
                else
                    assignments[left] = new List<ExpressionSyntax> { assignment.Right };
            }
        }

        var closure = new Dictionary<ISymbol, object?>(SymbolEqualityComparer.Default);

        // 种子：作用域内全部裸指针 stackalloc 局部（各自独立槽）。
        foreach (var (declarator, local) in symbols)
        {
            if (declarator.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() != null
                && TryGetStackAllocExpression(declarator, out _))
            {
                closure[local] = local;
            }
        }

        // 不动点扩展别名闭包。
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (declarator, local) in symbols)
            {
                if (closure.ContainsKey(local)) continue;

                var expressions = new List<ExpressionSyntax>();
                if (declarator.Initializer?.Value is { } initializer)
                    expressions.Add(initializer);
                if (assignments.TryGetValue(local, out var reassigned))
                    expressions.AddRange(reassigned);

                if (expressions.Count == 0) continue;

                object? joinedSlot = local;
                var provable = true;
                foreach (var expression in expressions)
                {
                    if (!TryResolveAliasSlot(expression, model, closure, out var expressionSlot))
                    {
                        provable = false;
                        break;
                    }

                    if (expressionSlot != null)
                        joinedSlot = expressionSlot;
                }

                if (provable)
                {
                    closure[local] = joinedSlot;
                    changed = true;
                }
            }
        }

        // 标记已入闭包但后续被不可证表达式重绑定的游标（槽置 null）。
        foreach (var (declarator, local) in symbols)
        {
            if (!closure.ContainsKey(local)) continue;

            // stackalloc 种子的初值即分配点（非别名表达式），其槽恒为自身，
            // 不参与别名可证性重验证；仅校验后续重绑定。
            var isSeed = declarator.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() != null
                && TryGetStackAllocExpression(declarator, out _);

            var expressions = new List<ExpressionSyntax>();
            if (!isSeed && declarator.Initializer?.Value is { } initializer)
                expressions.Add(initializer);
            if (assignments.TryGetValue(local, out var reassigned))
                expressions.AddRange(reassigned);

            if (expressions.Count == 0) continue;

            foreach (var expression in expressions)
            {
                if (!TryResolveAliasSlot(expression, model, closure, out _))
                {
                    closure[local] = null;
                    break;
                }
            }
        }

        // 补登记「被不可证初值根植的指针局部」为 null 槽，使引用点可判。
        foreach (var (declarator, local) in symbols)
        {
            if (closure.ContainsKey(local)) continue;
            if (declarator.Initializer?.Value is not { } initializer) continue;

            var rootSymbols = CollectPointerRoots(initializer, model);
            if (rootSymbols.Any(closure.ContainsKey)
                || initializer.DescendantNodesAndSelf()
                    .OfType<StackAllocArrayCreationExpressionSyntax>().Any()
                || initializer.DescendantNodesAndSelf()
                    .OfType<ImplicitStackAllocArrayCreationExpressionSyntax>().Any())
            {
                closure[local] = null;
            }
        }

        return closure;
    }

    /// <summary>
    /// 别名初值/重绑定表达式能否溯源到闭包游标：null 字面量（槽不变）、裸
    /// 闭包游标、<c>p ± n</c>、同槽条件、checked 包装、<c>&amp;p[k]</c>。
    /// </summary>
    private static bool TryResolveAliasSlot(
        ExpressionSyntax raw,
        SemanticModel model,
        Dictionary<ISymbol, object?> closure,
        out object? slot)
    {
        slot = null;
        var e = UnwrapWrappers(raw);

        if (e.IsKind(NullLiteralExpression))
            return true;

        if (e is ConditionalExpressionSyntax conditional)
        {
            if (!TryResolveAliasSlot(conditional.WhenTrue, model, closure, out var trueSlot)
                || !TryResolveAliasSlot(conditional.WhenFalse, model, closure, out var falseSlot)
                || trueSlot == null || falseSlot == null
                || !Equals(trueSlot, falseSlot))
            {
                return false;
            }

            slot = trueSlot;
            return true;
        }

        if (e is PrefixUnaryExpressionSyntax { RawKind: (int)AddressOfExpression } address
            && address.Operand is ElementAccessExpressionSyntax elementAccess
            && elementAccess.ArgumentList.Arguments.Count == 1)
        {
            return TryExpressionSlot(elementAccess.Expression, model, closure, out slot);
        }

        if (e is BinaryExpressionSyntax binary
            && binary.IsKind(AddExpression))
        {
            var leftType = model.GetTypeInfo(binary.Left).Type;
            var rightType = model.GetTypeInfo(binary.Right).Type;
            var pointerSide = leftType is IPointerTypeSymbol ? binary.Left
                : rightType is IPointerTypeSymbol ? binary.Right
                : null;
            if (pointerSide == null) return false;
            return TryExpressionSlot(pointerSide, model, closure, out slot);
        }

        if (e is BinaryExpressionSyntax { RawKind: (int)SubtractExpression } subtract
            && model.GetTypeInfo(subtract.Left).Type is IPointerTypeSymbol)
        {
            return TryExpressionSlot(subtract.Left, model, closure, out slot);
        }

        return TryExpressionSlot(e, model, closure, out slot);
    }

    private static bool TryExpressionSlot(
        ExpressionSyntax expression,
        SemanticModel model,
        Dictionary<ISymbol, object?> closure,
        out object? slot)
    {
        slot = null;
        expression = UnwrapWrappers(expression);

        if (expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            return false;
        if (model.GetSymbolInfo(expression).Symbol is not { } symbol) return false;
        if (!closure.TryGetValue(symbol, out var resolved) || resolved == null) return false;
        slot = resolved;
        return true;
    }

    private static HashSet<ISymbol> CollectPointerRoots(
        ExpressionSyntax expression, SemanticModel model)
    {
        var roots = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var identifier in expression.DescendantNodesAndSelf()
                     .OfType<ExpressionSyntax>()
                     .Where(e => e is IdentifierNameSyntax or MemberAccessExpressionSyntax))
        {
            if (model.GetSymbolInfo(identifier).Symbol is { } symbol
                && IsPointerSymbol(symbol))
            {
                roots.Add(symbol);
            }
        }

        return roots;
    }

    private static bool IsPointerSymbol(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type is IPointerTypeSymbol,
        IParameterSymbol parameter => parameter.Type is IPointerTypeSymbol,
        IFieldSymbol field => field.Type is IPointerTypeSymbol,
        _ => false,
    };

    // ---- 引用点分类 ----

    private static bool ClassifyUse(
        IdentifierNameSyntax identifier,
        SemanticModel model,
        PointerMethodCatalog catalog,
        Dictionary<ISymbol, object?> tracked,
        SyntaxNode scope,
        out string reason)
    {
        var symbol = model.GetSymbolInfo(identifier).Symbol!;
        if (tracked.TryGetValue(symbol, out var slot) && slot == null)
        {
            reason = $"pointer '{symbol.Name}' is rebound to a value whose array origin is not " +
                     "provable (escape).";
            return false;
        }

        // 上溯括号/checked/同源指针条件表达式，取得承载该引用的最外层指针值
        // 表达式及其消费上下文。
        var expression = (ExpressionSyntax)identifier;
        while (true)
        {
            var parent = expression.Parent;
            switch (parent)
            {
                case ParenthesizedExpressionSyntax or CheckedExpressionSyntax:
                    expression = (ExpressionSyntax)parent;
                    continue;
                case ConditionalExpressionSyntax conditional
                    when conditional.WhenTrue == expression || conditional.WhenFalse == expression:
                    if (!TryResolveAliasSlot(conditional, model, tracked, out _))
                    {
                        reason = $"pointer conditional '{conditional}' mixes different array sources.";
                        return false;
                    }

                    expression = conditional;
                    continue;
                default:
                    return ClassifyExpressionUse(expression, model, catalog, tracked, scope,
                        identifier, out reason);
            }
        }
    }

    private static bool ClassifyExpressionUse(
        ExpressionSyntax expression,
        SemanticModel model,
        PointerMethodCatalog catalog,
        Dictionary<ISymbol, object?> tracked,
        SyntaxNode scope,
        IdentifierNameSyntax originalIdentifier,
        out string reason)
    {
        reason = string.Empty;

        switch (expression.Parent)
        {
            // 下标读写：p[i]（receiver 旧类型为指针）。
            case ElementAccessExpressionSyntax access when access.Expression == expression:
                if (model.GetTypeInfo(access.Expression).Type is IPointerTypeSymbol)
                    return true;
                reason = $"stackalloc cursor '{originalIdentifier}' indexes a non-pointer receiver.";
                return false;

            // *p
            case PrefixUnaryExpressionSyntax { RawKind: (int)PointerIndirectionExpression }:
                return true;

            // ++/--p
            case PrefixUnaryExpressionSyntax prefix
                when prefix.IsKind(PreIncrementExpression) || prefix.IsKind(PreDecrementExpression):
                return true;

            // &p[k] 仅允许作为嵌套 fixed 帧初值。
            case PrefixUnaryExpressionSyntax { RawKind: (int)AddressOfExpression } address:
                if (IsFixedFrameInitializer(address)) return true;
                reason = $"address-of '{address}' escapes the stackalloc array index domain.";
                return false;

            // p++/p--
            case PostfixUnaryExpressionSyntax post
                when post.IsKind(PostIncrementExpression) || post.IsKind(PostDecrementExpression):
                return true;

            case BinaryExpressionSyntax binary:
                return ClassifyBinaryUse(binary, expression, model, tracked, out reason);

            case AssignmentExpressionSyntax assignment:
                return ClassifyAssignmentUse(assignment, expression, model, tracked, out reason);

            case ArgumentSyntax argument:
                return ClassifyArgumentUse(argument, expression, model, catalog, out reason);

            case ReturnStatementSyntax:
                reason = $"stackalloc cursor '{originalIdentifier}' is returned (escape).";
                return false;

            case ThrowExpressionSyntax or ThrowStatementSyntax:
                reason = $"stackalloc cursor '{originalIdentifier}' is thrown (escape).";
                return false;

            case CastExpressionSyntax:
                reason = $"cast involving stackalloc cursor '{originalIdentifier}' takes a pointer " +
                         "address; only same-array index forms are supported.";
                return false;

            case MemberAccessExpressionSyntax member
                when member.IsKind(PointerMemberAccessExpression)
                     || (member.Expression == expression
                         && member.IsKind(SimpleMemberAccessExpression)):
                reason = $"member access through stackalloc cursor '{originalIdentifier}' is not " +
                         "supported.";
                return false;

            case InvocationExpressionSyntax invocation when invocation.Expression == expression:
                reason = $"stackalloc cursor '{originalIdentifier}' is invoked as a method/function " +
                         "pointer (escape).";
                return false;

            case NameColonSyntax:
                // 命名实参名位不构成使用（标识符作为参数名）。
                return true;

            default:
                // nameof(p) 不求值，允许；其余未识别消费形态保守拒绝。
                if (IsInsideNameOf(expression)) return true;
                reason = $"stackalloc cursor '{originalIdentifier}' is used in an unsupported " +
                         $"form ('{expression.Parent?.Kind().ToString() ?? "?"}').";
                return false;
        }
    }

    private static bool ClassifyBinaryUse(
        BinaryExpressionSyntax binary,
        ExpressionSyntax operand,
        SemanticModel model,
        Dictionary<ISymbol, object?> tracked,
        out string reason)
    {
        reason = string.Empty;
        var leftType = model.GetTypeInfo(binary.Left).Type;
        var rightType = model.GetTypeInfo(binary.Right).Type;
        var leftPointer = leftType is IPointerTypeSymbol;
        var rightPointer = rightType is IPointerTypeSymbol;

        if (binary.IsKind(AddExpression))
        {
            // 指针 + int（两侧均可）。
            if (leftPointer ^ rightPointer)
                return RequireSingleTrackedSlot(binary, model, tracked, ref reason,
                    "pointer addition mixes an untracked pointer source.");
            reason = $"binary '{binary}' is not a supported pointer+integer addition.";
            return false;
        }

        if (binary.IsKind(SubtractExpression))
        {
            if (leftPointer && !rightPointer)
                return RequireSingleTrackedSlot(binary.Left, model, tracked, ref reason,
                    "pointer subtraction uses an untracked pointer source.");

            if (leftPointer && rightPointer)
            {
                if (!TryGetTrackedSlot(binary.Left, model, tracked, out var leftSlot)
                    || !TryGetTrackedSlot(binary.Right, model, tracked, out var rightSlot)
                    || leftSlot == null || rightSlot == null
                    || !Equals(leftSlot, rightSlot))
                {
                    reason = $"pointer subtraction '{binary}' is not between cursors of the same " +
                             "provable stackalloc array.";
                    return false;
                }

                return true;
            }

            reason = $"binary '{binary}' is not a supported pointer subtraction.";
            return false;
        }

        if (binary.IsKind(LessThanExpression) || binary.IsKind(GreaterThanExpression)
            || binary.IsKind(LessThanOrEqualExpression) || binary.IsKind(GreaterThanOrEqualExpression)
            || binary.IsKind(EqualsExpression) || binary.IsKind(NotEqualsExpression))
        {
            // p == null / p != null
            if (binary.Left.IsKind(NullLiteralExpression) || binary.Right.IsKind(NullLiteralExpression))
                return RequireSingleTrackedSlot(operand, model, tracked, ref reason,
                    "null comparison uses an untracked pointer source.");

            if (leftPointer && rightPointer)
            {
                if (!TryGetTrackedSlot(binary.Left, model, tracked, out var leftSlot)
                    || !TryGetTrackedSlot(binary.Right, model, tracked, out var rightSlot)
                    || leftSlot == null || rightSlot == null
                    || !Equals(leftSlot, rightSlot))
                {
                    reason = $"pointer comparison '{binary}' is not between cursors of the same " +
                             "provable stackalloc array.";
                    return false;
                }

                return true;
            }

            reason = $"pointer comparison '{binary}' mixes pointer and non-pointer operands.";
            return false;
        }

        reason = $"pointer arithmetic '{binary}' uses an unsupported operator.";
        return false;
    }

    private static bool ClassifyAssignmentUse(
        AssignmentExpressionSyntax assignment,
        ExpressionSyntax part,
        SemanticModel model,
        Dictionary<ISymbol, object?> tracked,
        out string reason)
    {
        reason = string.Empty;

        if (assignment.IsKind(AddAssignmentExpression) || assignment.IsKind(SubtractAssignmentExpression))
        {
            if (assignment.Left == part)
                return RequireSingleTrackedSlot(assignment.Left, model, tracked, ref reason,
                    "compound assignment targets an untracked pointer source.");
            return true; // int 右操作数
        }

        if (!assignment.IsKind(SimpleAssignmentExpression))
        {
            reason = $"assignment '{assignment}' on a stackalloc cursor uses an unsupported operator.";
            return false;
        }

        if (assignment.Left == part)
        {
            // 同局部重绑定已在闭包构造时验证；其余左值即外泄存储位。
            var leftSymbol = model.GetSymbolInfo(assignment.Left).Symbol;
            if (leftSymbol is ILocalSymbol && tracked.ContainsKey(leftSymbol))
                return true;

            reason = $"stackalloc cursor is assigned to '{assignment.Left}', which is not a " +
                     "tracked local (field/property/indexer escape).";
            return false;
        }

        // RHS 中的引用：左值必须是同闭包局部（否则即外泄，由左值非指针形态
        // 的根因判定在别处兜不住——此处直接拒绝非局部左值）。
        var targetSymbol = model.GetSymbolInfo(assignment.Left).Symbol;
        if (targetSymbol is ILocalSymbol targetLocal && tracked.ContainsKey(targetLocal))
            return true;

        reason = $"stackalloc cursor is forwarded through assignment '{assignment.Left}' (escape).";
        return false;
    }

    private static bool ClassifyArgumentUse(
        ArgumentSyntax argument,
        ExpressionSyntax argumentExpression,
        SemanticModel model,
        PointerMethodCatalog catalog,
        out string reason)
    {
        reason = string.Empty;

        if (argument.RefKindKeyword.Kind() != None)
        {
            reason = $"stackalloc cursor is passed by ref/out/in '{argument}' (escape).";
            return false;
        }

        if (argument.Parent is not ArgumentListSyntax argumentList)
        {
            reason = $"argument '{argument}' has an unsupported argument list shape.";
            return false;
        }

        if (argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            reason = $"stackalloc cursor argument '{argument}' is not passed to a supported " +
                     "method invocation (object creation/indexer/attribute are escapes).";
            return false;
        }

        var info = model.GetSymbolInfo(invocation);
        var methods = new List<IMethodSymbol>();
        if (info.Symbol is IMethodSymbol direct) methods.Add(direct);
        foreach (var candidate in info.CandidateSymbols)
        {
            if (candidate is IMethodSymbol candidateMethod) methods.Add(candidateMethod);
        }

        if (methods.Count == 0)
        {
            reason = $"invocation '{invocation}' cannot be resolved to a provable method.";
            return false;
        }

        if (methods.Any(PointerMethodCatalog.IsHolderCreateSymbol))
        {
            reason = $"stackalloc cursor is lifted into a ByRef<T*> holder '{invocation}' (escape).";
            return false;
        }

        var argumentType = model.GetTypeInfo(argumentExpression).Type;
        var isPointerArgument = argumentType is IPointerTypeSymbol;

        foreach (var method in methods)
        {
            // 含任何非白名单指针签名的被调方：R5 必以 UF021 拒绝（虚方/委托/
            // 元数据均无法按 paired-index 一致改写），保持 UF040 保守拒绝。
            foreach (var parameter in method.Parameters)
            {
                if (parameter.Type is IPointerTypeSymbol pointerParameter
                    && !PointerMethodCatalog.IsWhitelistElement(pointerParameter.PointedAtType))
                {
                    reason = $"callee '{method.Name}' has a non-whitelist pointer parameter " +
                             $"'{parameter.Name}' (void*/generic/struct pointer).";
                    return false;
                }
            }

            if (method.ReturnType is IPointerTypeSymbol returnedPointer
                && !PointerMethodCatalog.IsWhitelistElement(returnedPointer.PointedAtType))
            {
                reason = $"callee '{method.Name}' returns a non-whitelist pointer.";
                return false;
            }

            var inSource = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                is MethodDeclarationSyntax or LocalFunctionStatementSyntax;

            if (inSource)
            {
                // 源码方法必须已被配对（含信号⑦经 stackalloc 可证根强制）。
                if (catalog.GetPlan(method) == null)
                {
                    reason = $"in-source callee '{method.Name}' receives a stackalloc cursor but is " +
                             "not rewritten under the paired array+index convention.";
                    return false;
                }
            }
            else if (isPointerArgument)
            {
                // 元数据/外部方法：仅当对应形参是白名单指针（R6 既有 int 索引
                // 实参通道，如 Encoding.UTF8.GetBytes）才放行。
                if (!TryGetParameterForArgument(method, argument, invocation.ArgumentList,
                        out var matchedParameter)
                    || matchedParameter is not { Type: IPointerTypeSymbol matchedPointer }
                    || !PointerMethodCatalog.IsWhitelistElement(matchedPointer.PointedAtType))
                {
                    reason = $"external callee '{method.Name}' has no whitelisted pointer " +
                             "parameter matching this stackalloc cursor argument.";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>按位置/命名实参匹配形参（params 数组形态不支持，返回 null）。</summary>
    private static bool TryGetParameterForArgument(
        IMethodSymbol method,
        ArgumentSyntax argument,
        ArgumentListSyntax argumentList,
        out IParameterSymbol? parameter)
    {
        parameter = null;

        if (argument.NameColon?.Name is { } named)
        {
            parameter = method.Parameters.FirstOrDefault(p => p.Name == named.Identifier.ValueText);
            return parameter != null;
        }

        var index = argumentList.Arguments.IndexOf(argument);
        if (index < 0) return false;
        if (index >= method.Parameters.Length) return false;

        var candidate = method.Parameters[index];
        if (candidate.IsParams) return false;
        parameter = candidate;
        return true;
    }

    private static bool RequireSingleTrackedSlot(
        ExpressionSyntax expression,
        SemanticModel model,
        Dictionary<ISymbol, object?> tracked,
        ref string reason,
        string failure)
    {
        if (TryGetTrackedSlot(expression, model, tracked, out var slot) && slot != null)
            return true;
        reason = failure;
        return false;
    }

    /// <summary>
    /// 表达式中的指针根是否全部落在 tracked 闭包且共享唯一源槽。
    /// </summary>
    private static bool TryGetTrackedSlot(
        ExpressionSyntax rawExpression,
        SemanticModel model,
        Dictionary<ISymbol, object?> tracked,
        out object? slot)
    {
        slot = null;
        object? found = null;

        foreach (var root in CollectPointerRoots(rawExpression, model))
        {
            if (!tracked.TryGetValue(root, out var rootSlot) || rootSlot == null)
                return false;
            if (found != null && !Equals(found, rootSlot)) return false;
            found = rootSlot;
        }

        slot = found;
        return found != null;
    }

    private static bool IsFixedFrameInitializer(ExpressionSyntax addressOf)
    {
        var current = addressOf.Parent;
        while (current is ParenthesizedExpressionSyntax or CheckedExpressionSyntax)
            current = current.Parent;

        if (current is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator })
            return declarator.FirstAncestorOrSelf<FixedStatementSyntax>() != null;

        return false;
    }

    private static bool IsInsideNameOf(ExpressionSyntax expression)
    {
        var current = expression.Parent;
        while (current is ArgumentSyntax argument
               && argument.Parent is ArgumentListSyntax list
               && list.Parent is InvocationExpressionSyntax invocation)
        {
            return invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" };
        }

        return false;
    }

    private static ExpressionSyntax UnwrapWrappers(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case CheckedExpressionSyntax checkedExpression:
                    expression = checkedExpression.Expression;
                    continue;
                default:
                    return expression;
            }
        }
    }

    /// <summary>
    /// 若声明初值（可去括号）是显式/隐式 stackalloc，输出该节点。
    /// </summary>
    public static bool TryGetStackAllocExpression(
        VariableDeclaratorSyntax declarator,
        out ExpressionSyntax stackAlloc)
    {
        stackAlloc = null!;
        if (declarator.Initializer?.Value is not { } raw) return false;
        var initializer = UnwrapWrappers(raw);

        switch (initializer)
        {
            case StackAllocArrayCreationExpressionSyntax explicitStack:
                stackAlloc = explicitStack;
                return true;
            case ImplicitStackAllocArrayCreationExpressionSyntax implicitStack:
                stackAlloc = implicitStack;
                return true;
            default:
                return false;
        }
    }
}
