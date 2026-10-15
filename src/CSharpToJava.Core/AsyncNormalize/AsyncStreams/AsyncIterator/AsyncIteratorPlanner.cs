using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams.AsyncIterator;

/// <summary>
/// AS-6 异步迭代器计划器（设计 D7、§3.2、§4.4）。在 SemanticModel 上枚举
/// async + IAsyncEnumerable/IEnumerator 返回 + yield 的方法/局部函数：
/// <list type="bullet">
/// <item>可降级方法（v1 子集）→ <see cref="AsyncIteratorPlan"/>；</item>
/// <item>局部函数 / kill-switch / v1 复杂形态 → CS2J1844、成员整体冻结；</item>
/// <item>成员跨度内已有基线编译错误（CS1623/CS1625/CS1626/CS1631/CS1621…）
/// → 静默冻结，不抢诊断、零改写（基线优先纪律）。</item>
/// </list>
/// 调用点：① post-C18 模型（冻结集供 foreach/using 站点跳过 + 诊断）；
/// ② await foreach/using 展开后的重绑模型（仅取计划做联合状态机降级）。
/// 两次枚举均按源序，序号/命名稳定。
/// </summary>
internal static class AsyncIteratorPlanner
{
    private const string EnumeratorCancellationAttributeName =
        "System.Runtime.CompilerServices.EnumeratorCancellationAttribute";

    public static AsyncIteratorAnalysis Analyze(
        SemanticModel model,
        AsyncNormalizeOptions options,
        INamedTypeSymbol? asyncEnumerableInterface,
        INamedTypeSymbol? asyncEnumeratorInterface,
        List<AsyncNormalizeDiagnostic> diagnostics)
    {
        var plans = new List<AsyncIteratorPlan>();
        var frozen = new List<SyntaxNode>();

        var root = model.SyntaxTree.GetRoot();
        var baselineErrors = CollectBaselineErrors(model);
        var usedNamesByType = new Dictionary<INamedTypeSymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        var index = 0;

        foreach (var node in root.DescendantNodes())
        {
            MethodDeclarationSyntax? method = node as MethodDeclarationSyntax;
            LocalFunctionStatementSyntax? local = node as LocalFunctionStatementSyntax;
            if (method == null && local == null)
                continue;

            if (model.GetDeclaredSymbol(node!) is not IMethodSymbol symbol || !symbol.IsAsync)
                continue;

            var containsYield = NodeContainsYield(node!);
            if (!containsYield)
                continue;

            // C18 提升产物（合成方法）或非严格 skip 标注节点：其 1844 已在首轮对
            // 原始 local function 发过——后续重绑时静默冻结、不重复诊断、不降级，
            // 体内站点（如 await foreach）保持原样。
            if (node!.HasAnnotations(AsyncNormalizeAnnotations.AnnotationKind)
                || node!.HasAnnotations(AsyncNormalizeAnnotations.SkipAnnotationKind))
            {
                frozen.Add(node!);
                continue;
            }

            // 局部函数形态（合法 C# 形态，v1 不降级）先于基线静默直接 1844。
            if (local != null)
            {
                frozen.Add(local);
                Emit(diagnostics, local, "local function", local.Identifier.ValueText, symbol,
                    "async-iterator local functions are not lowered in v1 (the host member is "
                    + "left unchanged)");
                continue;
            }

            // 硬形状（yield in try-catch/finally/lock，CS1625/CS1626/CS1631 族）：
            // 即使 Roslyn 已报基线错误也发 CS2J1844——这些形态本就是 v1 拒绝目标，
            // 测试/文档按 1844 承诺承接，不依赖基线码。
            var hardShape = UnsupportedYieldShape(node!);
            if (hardShape != null)
            {
                frozen.Add(method!);
                Emit(diagnostics, method!, "method", method!.Identifier.ValueText, symbol, hardShape);
                continue;
            }

            // 基线优先：跨度内已有其他编译错误（CS1623/CS1621/CS1622 等）→ 不抢
            // 诊断、不改写。
            if (BaselineContains(baselineErrors, model.SyntaxTree, node!.Span))
            {
                frozen.Add(node!);
                continue;
            }

            var loweringEnabled = options.EnableAsyncIteratorLowering
                && options.EnableAwaitForeach
                && options.EnableAwaitUsing;
            if (options.RejectAsyncIterator || !loweringEnabled)
            {
                frozen.Add(method!);
                Emit(diagnostics, method!, "method", method!.Identifier.ValueText, symbol,
                    "async-iterator lowering is disabled (RejectAsyncIterator kill-switch or an "
                    + "async-stream sub-option is off); the member is left unchanged");
                continue;
            }

            var reason = Validate(method!, symbol, model, asyncEnumerableInterface,
                asyncEnumeratorInterface);
            if (reason != null)
            {
                frozen.Add(method!);
                Emit(diagnostics, method!, "method", method!.Identifier.ValueText, symbol, reason);
                continue;
            }

            var shape = ResolveStreamShape(symbol.ReturnType,
                asyncEnumerableInterface, asyncEnumeratorInterface)!.Value;
            var elementType = shape.Element;
            var isEnumerable = shape.IsEnumerable;
            var containingType = symbol.ContainingType;
            if (!usedNamesByType.TryGetValue(containingType, out var usedNames))
            {
                usedNames = new HashSet<string>(StringComparer.Ordinal);
                if (method!.Parent is TypeDeclarationSyntax hostType)
                {
                    foreach (var member in hostType.Members.OfType<BaseTypeDeclarationSyntax>())
                        usedNames.Add(member.Identifier.ValueText);
                }
                usedNamesByType.Add(containingType, usedNames);
            }

            var hostName = containingType.Name;
            var tick = hostName.IndexOf('`');
            if (tick >= 0)
                hostName = hostName[..tick];

            var enumeratorName = AsyncStreamNaming.GetIteratorClassName(hostName, index, usedNames);
            string? enumerableName = null;
            if (isEnumerable)
                enumerableName = AsyncStreamNaming.GetEnumerableClassName(hostName, index, usedNames);

            var parameters = new List<AsyncIteratorParameter>();
            IParameterSymbol? ctMerge = null;
            foreach (var parameter in symbol.Parameters)
            {
                var fieldName = AsyncStreamNaming.GetIteratorParameterField(index, parameter.Name);
                var hasEnumeratorCancellation = parameter.GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(
                        a.AttributeClass?.OriginalDefinition,
                        model.Compilation.GetTypeByMetadataName(EnumeratorCancellationAttributeName)));
                parameters.Add(new AsyncIteratorParameter(parameter, fieldName, hasEnumeratorCancellation));
                if (hasEnumeratorCancellation
                    && AsyncTypeResolver.IsCancellationTokenTyped(parameter.Type))
                {
                    ctMerge ??= parameter;
                }
            }

            var yieldReturnCount = method!.DescendantNodes(OutsideNestedFunctions)
                .OfType<YieldStatementSyntax>()
                .Count(y => y.IsKind(SyntaxKind.YieldReturnStatement));

            plans.Add(new AsyncIteratorPlan(
                method, symbol, index, enumeratorName, enumerableName,
                elementType, isEnumerable, containingType, parameters, ctMerge,
                yieldReturnCount));
            index++;
        }

        return new AsyncIteratorAnalysis(plans, frozen, diagnostics);
    }

    /// <summary>降级前重绑模型上的二趟：只取计划（诊断以首轮为准，不重复发）。</summary>
    public static IReadOnlyList<AsyncIteratorPlan> AnalyzePlans(
        SemanticModel model,
        AsyncNormalizeOptions options,
        INamedTypeSymbol? asyncEnumerableInterface,
        INamedTypeSymbol? asyncEnumeratorInterface)
    {
        var sink = new List<AsyncNormalizeDiagnostic>();
        return Analyze(model, options, asyncEnumerableInterface, asyncEnumeratorInterface, sink).Plans;
    }

    // ── v1 合法性闸门（命中即 1844；基线已错的形态在调用前被静默冻结） ──

    /// <summary>
    /// 硬形状拒绝（先于基线静默）：yield 落入 try-catch/finally/lock
    /// （CS1625/CS1626/CS1631 族）。对方法节点与局部函数节点均可调用。
    /// </summary>
    private static string? UnsupportedYieldShape(SyntaxNode host)
    {
        foreach (var tryStatement in host.DescendantNodes(RootSafe(host))
                     .OfType<TryStatementSyntax>())
        {
            if (tryStatement.Catches.Count > 0 && ContainsYield(tryStatement))
                return "'yield' inside a try/catch region is not supported in v1 (CS1626 shape; "
                    + "only try/finally regions are lowered)";
            if (tryStatement.Finally != null && ContainsYield(tryStatement.Finally))
                return "'yield' inside a 'finally' block is illegal (CS1625 shape)";
        }

        foreach (var @lock in host.DescendantNodes(RootSafe(host))
                     .OfType<LockStatementSyntax>())
        {
            if (ContainsYield(@lock) || ContainsReturn(@lock))
                return "'yield'/'return' inside a 'lock' region is not supported in v1 (CS1631 shape)";
        }

        return null;
    }

    /// <summary>
    /// 遍历谓词包装：<see cref="SyntaxNode.DescendantNodes(System.Func{Microsoft.CodeAnalysis.SyntaxNode,bool})"/>
    /// 会先对起始节点自身求值，起始节点本身若是局部函数会被 OutsideNestedFunctions
    /// 直接挡掉（体内一切不可见）；起始节点始终放行。
    /// </summary>
    private static Func<SyntaxNode, bool> RootSafe(SyntaxNode root)
        => node => node == root || OutsideNestedFunctions(node);

    private static string? Validate(
        MethodDeclarationSyntax method,
        IMethodSymbol symbol,
        SemanticModel model,
        INamedTypeSymbol? asyncEnumerableInterface,
        INamedTypeSymbol? asyncEnumeratorInterface)
    {
        if (ResolveStreamShape(symbol.ReturnType, asyncEnumerableInterface,
                asyncEnumeratorInterface) is null)
        {
            return "the return type is not 'System.Collections.Generic.IAsyncEnumerable<T>'/"
                + "'IAsyncEnumerator<T>' (a custom async-iterator builder type cannot be lowered in v1)";
        }

        if (method.Body is null)
            return "expression-bodied members cannot be lowered (a block body with 'yield' is required)";

        if (method.Parent is not TypeDeclarationSyntax)
            return "only methods declared in class/record/struct declarations are lowered in v1";

        if (symbol.ContainingType is not { IsReferenceType: true, IsTupleType: false })
            return "async-iterator methods declared in value types (structs) are not lowered in v1";

        // 扩展方法按静态工厂降级：第一个 this 形参与其余形参一同提升为枚举器字段，
        // 无需特殊处理（静态方法无 __cs2jThis 字段）。

        foreach (var parameter in symbol.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
                return $"ref/in/out parameter '{parameter.Name}' cannot be hoisted (CS1623 shape)";
            if (parameter.ScopedKind != ScopedKind.None)
                return $"scoped parameter '{parameter.Name}' cannot be hoisted in v1";
        }

        var body = method.Body;

        if (body.DescendantNodes(OutsideNestedFunctions).Any(n => n is LocalFunctionStatementSyntax))
            return "a residual local function remains in the member (lift it first or inline it)";

        if (body.DescendantNodes(OutsideNestedFunctions).Any(n =>
                n is GotoStatementSyntax or LabeledStatementSyntax))
            return "user-defined 'goto'/labels in the member are not supported in v1";

        if (body.DescendantNodes(OutsideNestedFunctions).Any(n => n is BaseExpressionSyntax))
            return "'base.' member access in the member is not supported in v1";

        if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword))
            || body.DescendantNodes(OutsideNestedFunctions).Any(n =>
                n is UnsafeStatementSyntax or FixedStatementSyntax))
            return "unsafe/fixed contexts are not supported in v1";

        // ref/ref-readonly 局部（含 ref struct 活性提升的前置检查）：
        // `ref T x = ref …` 与 `ref readonly T x` 的声明类型均为 RefTypeSyntax。
        if (body.DescendantNodes(OutsideNestedFunctions)
            .OfType<LocalDeclarationStatementSyntax>()
            .Any(local => local.Declaration.Type is RefTypeSyntax
                || local.Modifiers.Any(SyntaxKind.RefKeyword)))
        {
            return "ref locals cannot be hoisted in v1";
        }

        // 内联 out var / 解构 / 模式变量：声明形态无法机械提升为字段，v1 拒绝。
        if (body.DescendantNodes(OutsideNestedFunctions).Any(n =>
                n is DeclarationExpressionSyntax
                    or ParenthesizedVariableDesignationSyntax
                    or DeclarationPatternSyntax
                    or DiscardDesignationSyntax))
            return "inline 'out var' declarations, deconstruction, or pattern variables are "
                + "not supported in v1";

        // 同步 using 声明（作用域折叠到块末）在 v1 不做区域化；语句形态支持。
        if (body.DescendantNodes(OutsideNestedFunctions).Any(n =>
                n is LocalDeclarationStatementSyntax { UsingKeyword: var u } l
                && u.IsKind(SyntaxKind.UsingKeyword)
                && !l.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)))
            return "declaration-form synchronous 'using' with 'yield' in scope is not supported "
                + "in v1 (rewrite it as statement-form 'using (…) { … }')";

        // try/catch、try-finally 内 yield、lock 内 yield/return 等硬形状由
        // UnsupportedYieldShape 在 Validate 之前统一拒绝（含基线已错形态）。

        // switch 仅支持常量/默认标签、无 when、无 goto case。
        foreach (var @switch in body.DescendantNodes(OutsideNestedFunctions)
                     .OfType<SwitchStatementSyntax>())
        {
            foreach (var section in @switch.Sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label is not (CaseSwitchLabelSyntax or DefaultSwitchLabelSyntax))
                        return "pattern/relational 'switch' labels are not supported in v1";
                }
            }
        }
        if (body.DescendantNodes(OutsideNestedFunctions)
            .Any(n => n.IsKind(SyntaxKind.GotoCaseStatement)
                || n.IsKind(SyntaxKind.GotoDefaultStatement)))
        {
            return "'goto case' in a switch is not supported in v1";
        }

        // 解构 foreach。
        if (body.DescendantNodes(OutsideNestedFunctions).Any(n => n is ForEachVariableStatementSyntax))
            return "deconstruction-form 'foreach' is not supported in v1";

        // 块外直接声明（switch section 共享作用域）。
        foreach (var local in body.DescendantNodes(OutsideNestedFunctions)
                     .OfType<LocalDeclarationStatementSyntax>())
        {
            if (local.Parent is not BlockSyntax and not SwitchSectionSyntax)
                return "local declarations in non-block scopes are not supported in v1";
        }

        // ref struct 活性提升：跨 yield 存活局部/资源/枚举器不能进字段。
        foreach (var localSymbol in EnumerateHoistCandidates(method, model))
        {
            if (localSymbol.Type.IsRefLikeType)
                return $"ref struct local '{localSymbol.Name}' cannot be hoisted to an "
                    + "enumerator field in v1";
        }

        foreach (var usingStatement in body.DescendantNodes(OutsideNestedFunctions)
                     .OfType<UsingStatementSyntax>()
                     .Where(u => !u.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) && ContainsYield(u)))
        {
            var type = UsingResourceType(usingStatement, model);
            if (type is { IsRefLikeType: true })
                return "a 'using' resource of ref struct type cannot be hoisted in v1";
        }

        foreach (var foreachStatement in body.DescendantNodes(OutsideNestedFunctions)
                     .OfType<ForEachStatementSyntax>()
                     .Where(f => !f.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) && ContainsYield(f)))
        {
            var info = model.GetForEachStatementInfo(foreachStatement);
            if (info.GetEnumeratorMethod is null)
                return "a 'foreach' enumerator could not be bound in v1";
            if (info.GetEnumeratorMethod.IsExtensionMethod)
                return "pattern/extension-based 'foreach' enumerators are not supported in v1";
            if (info.GetEnumeratorMethod.ReturnType is { IsRefLikeType: true })
                return "a ref struct 'foreach' enumerator cannot be hoisted in v1";
        }

        return null;
    }

    private static (ITypeSymbol Element, bool IsEnumerable)? ResolveStreamShape(
        ITypeSymbol returnType,
        INamedTypeSymbol? asyncEnumerableInterface,
        INamedTypeSymbol? asyncEnumeratorInterface)
    {
        if (returnType is not INamedTypeSymbol named || named.TypeArguments.Length != 1)
            return null;

        if (AsyncTypeResolver.IsAsyncEnumerable(returnType, asyncEnumerableInterface))
            return (named.TypeArguments[0], true);
        if (AsyncTypeResolver.IsAsyncEnumerator(returnType, asyncEnumeratorInterface))
            return (named.TypeArguments[0], false);
        return null;
    }

    /// <summary>
    /// 保守活性候选枚举（与 <see cref="AsyncIteratorLowering"/> 的提升判定同口径）：
    /// 形参全部；局部在「未被无 yield/return 的完整保留构造包裹」时全部提升。
    /// </summary>
    internal static IEnumerable<ILocalSymbol> EnumerateHoistCandidates(
        MethodDeclarationSyntax method, SemanticModel model)
    {
        var body = method.Body!;
        var intactBoundaries = CollectIntactBoundaries(body);

        foreach (var declarator in body.DescendantNodes(OutsideNestedFunctions)
                     .OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Parent is VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax }
                && model.GetDeclaredSymbol(declarator) is ILocalSymbol local
                && !IsInsideAny(declarator, intactBoundaries))
            {
                yield return local;
            }
        }

        foreach (var forEach in body.DescendantNodes(OutsideNestedFunctions)
                     .OfType<ForEachStatementSyntax>())
        {
            if (!IsInsideAny(forEach, intactBoundaries)
                && model.GetDeclaredSymbol(forEach) is ILocalSymbol foreachVariable)
            {
                yield return foreachVariable;
            }
        }

        foreach (var forStatement in body.DescendantNodes(OutsideNestedFunctions)
                     .OfType<ForStatementSyntax>())
        {
            if (forStatement.Declaration is null || IsInsideAny(forStatement, intactBoundaries))
                continue;
            foreach (var variable in forStatement.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(variable) is ILocalSymbol forVariable)
                    yield return forVariable;
            }
        }
    }

    /// <summary>
    /// 完整保留构造：自身（忽略嵌套函数/lambda）不含任何 yield/return 的
    /// 循环/try/using/switch/lock——其内部不做扁平化，局部无需提升。
    /// </summary>
    internal static List<SyntaxNode> CollectIntactBoundaries(BlockSyntax body)
    {
        var result = new List<SyntaxNode>();
        foreach (var node in body.DescendantNodes(OutsideNestedFunctions))
        {
            switch (node)
            {
                case ForEachStatementSyntax:
                case ForStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CommonForEachStatementSyntax:
                case TryStatementSyntax:
                case UsingStatementSyntax:
                case SwitchStatementSyntax:
                case LockStatementSyntax:
                    if (!ContainsYield(node) && !ContainsReturn(node))
                        result.Add(node);
                    break;
            }
        }
        return result;
    }

    internal static bool IsInsideAny(SyntaxNode node, List<SyntaxNode> boundaries)
        => boundaries.Any(b => b.Contains(node));

    private static ITypeSymbol? UsingResourceType(UsingStatementSyntax usingStatement, SemanticModel model)
    {
        if (usingStatement.Expression != null)
        {
            return model.GetTypeInfo(usingStatement.Expression).Type
                ?? model.GetTypeInfo(usingStatement.Expression).ConvertedType;
        }
        if (usingStatement.Declaration != null)
        {
            foreach (var variable in usingStatement.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(variable) is ILocalSymbol local)
                    return local.Type;
            }
        }
        return null;
    }

    private static void Emit(
        List<AsyncNormalizeDiagnostic> diagnostics,
        SyntaxNode node,
        string kind,
        string name,
        IMethodSymbol symbol,
        string reason)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Error,
            AsyncNormalizeDiagnosticCode.CS2J1844,
            $"Async iterator {kind} '{name}' (async + '{symbol.ReturnType.ToDisplayString()}' "
            + $"return type + 'yield' in the body) cannot be normalized: {reason}. The whole "
            + "member is left unchanged (no partial rewrite of its await foreach/await using "
            + "sites). Workarounds: eagerly materialize the sequence into a 'List<T>' from a "
            + "non-iterator helper, or hand-write an IAsyncEnumerator<T>.",
            Line: line));
    }

    private static bool NodeContainsYield(SyntaxNode node)
        => ContainsYield(node);

    internal static bool ContainsYield(SyntaxNode node)
        => node.DescendantNodes(RootSafe(node)).Any(n =>
            n.IsKind(SyntaxKind.YieldReturnStatement) || n.IsKind(SyntaxKind.YieldBreakStatement));

    internal static bool ContainsReturn(SyntaxNode node)
        => node.DescendantNodes(RootSafe(node))
            .Any(n => n.IsKind(SyntaxKind.ReturnStatement));

    /// <summary>遍历谓词：不进入局部函数/匿名函数/lambda（其体不属于本迭代器状态机）。</summary>
    internal static Func<SyntaxNode, bool> OutsideNestedFunctions { get; } = n => n is not (
        LocalFunctionStatementSyntax
        or AnonymousMethodExpressionSyntax
        or ParenthesizedLambdaExpressionSyntax
        or SimpleLambdaExpressionSyntax);

    private static List<(SyntaxTree Tree, TextSpan Span)> CollectBaselineErrors(SemanticModel model)
        => AsyncNormalizeAnalyzer.CollectTreeErrorSpans(model, null);

    private static bool BaselineContains(
        List<(SyntaxTree Tree, TextSpan Span)> baselineErrors,
        SyntaxTree tree,
        TextSpan outerSpan)
        => baselineErrors.Any(e =>
            e.Tree == tree
            && e.Span.Start >= outerSpan.Start
            && e.Span.End <= outerSpan.End);
}
