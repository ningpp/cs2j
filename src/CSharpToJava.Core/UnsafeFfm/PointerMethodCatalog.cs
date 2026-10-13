using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// 配对索引（paired-index）指针方法分析：判定一个方法是否需要把指针形参降级为
/// 「数组 + int 索引」配对、指针返回降级为 int 索引返回，并给出每个形参的
/// 具体计划。R5/R3/R6 共享同一分析结论，保证声明点与同编译所有调用点一致改写。
/// <para>
/// 统一判定规则（全部基于语义符号，无文件名/方法名白名单）：方法满足以下任一
/// 信号即配对——
/// <list type="bullet">
/// <item>① 返回白名单元素指针（游标必须以索引返回）；</item>
/// <item>② 含 holder 指针形参 <c>Cs2jByRef.ByRef&lt;T*&gt;</c>（holder 降级为
/// <c>ByRef&lt;int&gt;</c>，另需伴生数组形参承载解引用）；</item>
/// <item>③④ 方法的<b> fixed 块内</b>存在指针局部以指针形参为根
/// （<c>T* q = p[/p±n]</c>，入参游标进入 fixed 域），或指针加减/比较/相减
/// 以本方法指针形参为根操作数（入参索引彼此运算，数组-only 形参无法表达偏移）；
/// 仅扫描 fixed 块：不持有 fixed 缓冲区、直接在入参指针上运算的「纯指针形参」
/// 方法属于另一子族（跨形参同源推断），不在此配对；</item>
/// <item>⑤ 方法体内出现 <c>ByRef&lt;T*&gt;.Create(...)</c> 访问器提升
/// （本方法指针游标经 holder 传入配对被调方）；</item>
/// <item>⑥ 方法把自己的指针形参（含其 ±n 偏移）作为指针实参传给另一个已配对
/// 方法（调用约定沿调用图传播；指针实参仅根植于 fixed/局部游标时不要求配对，
/// R3 降级表即可承载）。</item>
/// </list>
/// 配对方法中<b>每个</b>白名单指针形参统一降级为 <c>T[] p, int pIndex</c>；
/// holder 形参降级为 <c>ByRef&lt;int&gt; h, T[] hArray</c>。入参同源关系是
/// 指针方法的既有入口契约（形参解引用本就无条件信任调用方数组），形参索引间、
/// 形参索引与本方法 fixed 索引间的比较/相减按 int 索引运算归一。
/// </para>
/// <para>
/// 不配对的形态：抽象/extern/virtual/override/接口/委托/元数据方法（声明不在
/// 本编译源码中，无法一致改写所有实现/调用点）、含非白名单指针签名的方法
/// （交 R5 既有 UF021 路径）、不持有 fixed 缓冲也不参与配对调用约定的纯指针
/// 形参方法（交既有 R5 array-only/R6 同源判定路径）。
/// </para>
/// </summary>
internal sealed class PointerMethodCatalog
{
    /// <summary>
    /// R5 挂到配对方法改写节点上的注解，Data 为原方法符号的 SymbolKey
    /// 字符串。签名（形参增删、指针→数组/int）被 R5 改写后，红节点无法再
    /// 经 OriginalSyntaxResolver 等价映射回原始声明，R3/R6 据此取回符号。
    /// </summary>
    public const string PairedMethodAnnotation = "c06-paired-method";

    private readonly Compilation _compilation;
    private readonly Dictionary<IMethodSymbol, PairedMethodPlan?> _cache =
        new(SymbolEqualityComparer.Default);

    /// <summary>信号⑥ 递归传播时的在途方法（调用图环保护）。</summary>
    private readonly HashSet<IMethodSymbol> _inProgress =
        new(SymbolEqualityComparer.Default);

    /// <summary>
    /// R5 配对声明节点注解 id → 原方法符号（catalog 随单次 RewriteFile 存活；
    /// 本工程 Roslyn 包不暴露 SymbolKey 公共 API，用进程内标识承载）。
    /// </summary>
    private readonly Dictionary<string, IMethodSymbol> _annotationSymbols = new();
    private int _annotationSequence;

    public PointerMethodCatalog(Compilation compilation)
    {
        _compilation = compilation;
    }

    /// <summary>
    /// R5 在配对方法改写后的声明节点上挂注解，Data 为本 catalog 内的临时
    /// 标识；R3/R6 经 <see cref="TryGetPlanFromAnnotations"/> 取回计划。
    /// </summary>
    public TNode AnnotatePaired<TNode>(TNode node, IMethodSymbol method)
        where TNode : SyntaxNode
    {
        var id = "m" + (_annotationSequence++).ToString();
        _annotationSymbols[id] = method;
        return node.WithAdditionalAnnotations(
            new SyntaxAnnotation(PairedMethodAnnotation, id));
    }

    /// <summary>
    /// R3/R6 取回配对计划的注解路径：R5 改写签名后，声明红节点无法再经
    /// OriginalSyntaxResolver 等价映射回原始声明（形参增删 + 返回类型改变），
    /// 改读 R5 挂在节点上的注解。
    /// </summary>
    public PairedMethodPlan? TryGetPlanFromAnnotations(SyntaxNode node)
    {
        var annotation = node.GetAnnotations(PairedMethodAnnotation).FirstOrDefault();
        if (annotation?.Data is not { Length: > 0 } id) return null;
        if (!_annotationSymbols.TryGetValue(id, out var method)) return null;
        return GetPlan(method);
    }

    /// <summary>
    /// 取方法的配对计划；不配对（含无法判定）返回 null。同一 RewriteFile 内
    /// 按符号缓存；跨文件声明通过其语法树的语义模型现场分析。
    /// </summary>
    public PairedMethodPlan? GetPlan(IMethodSymbol? method)
    {
        if (method == null) return null;
        if (_cache.TryGetValue(method, out var cached)) return cached;

        // 调用图环：在途方法按「配对」返回临时候选（其形参计划与最终计划仅
        // 取决于签名，命名确定一致），不写入缓存。
        if (_inProgress.Contains(method) && TryBuildSkeleton(method, out var cyclic))
            return cyclic;

        var plan = BuildPlan(method);
        _cache[method] = plan;
        return plan;
    }

    private bool TryBuildSkeleton(IMethodSymbol method, out PairedMethodPlan? plan)
    {
        if (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax))
        {
            plan = null;
            return false;
        }

        var paramPlans = BuildParamPlans(method);
        var returnsPointer = method.ReturnType is IPointerTypeSymbol;
        plan = FinalizePlan(method, paramPlans, returnsPointer);
        return true;
    }

    private PairedMethodPlan? BuildPlan(IMethodSymbol method)
    {
        if (method.IsAbstract || method.IsExtern || method.IsVirtual || method.IsOverride)
            return null;
        if (method.ContainingType?.TypeKind == TypeKind.Interface)
            return null;
        if (method.MethodKind is MethodKind.DelegateInvoke
            or MethodKind.AnonymousFunction)
            return null;

        var reference = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference?.GetSyntax() is not SyntaxNode declarationNode
            || declarationNode is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax))
        {
            return null;
        }

        var declaration = declarationNode;

        var treeModel = _compilation.GetSemanticModel(reference.SyntaxTree);

        // 含非白名单指针签名：交 R5 既有 UF021 拒绝路径，不做配对。
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type is IPointerTypeSymbol parameterPointer
                && !IsWhitelistElement(parameterPointer.PointedAtType))
            {
                return null;
            }

            if (parameter.Type is INamedTypeSymbol holder
                && IsByRefHolder(holder, out var holderElement)
                && !IsWhitelistElement(holderElement))
            {
                return null;
            }
        }

        var returnPointer = method.ReturnType as IPointerTypeSymbol;
        var returnsPointer = returnPointer != null;
        if (returnsPointer && !IsWhitelistElement(returnPointer!.PointedAtType))
            return null;

        var paramPlans = BuildParamPlans(method);
        var hasHolder = paramPlans.Any(p => p.Kind == PairedParamKind.HolderPointer);
        var hasPointerParam = paramPlans.Any(p => p.Kind == PairedParamKind.PlainPointer);

        bool paired;
        // 指针返回的配对约定需要「同签名内可索引的数组」：至少一个指针形参
        //（plain 伴生数组或 holder 伴生数组）。无任何指针形参的 T* M() 无法
        // 在调用点给出结果数组，仍走 array-only 旧路径（T[] M()）。
        if (hasHolder || (returnsPointer && hasPointerParam))
        {
            paired = true;
        }
        else if (!hasPointerParam)
        {
            paired = false;
        }
        else
        {
            _inProgress.Add(method);
            try
            {
                paired = BodyHasFixedCursorSignal(declaration, treeModel, paramPlans)
                    || BodyHasHolderAccessor(declaration, treeModel)
                    || BodyForwardsToPairedMethod(declaration, treeModel, paramPlans);
            }
            finally
            {
                _inProgress.Remove(method);
            }
        }

        if (!paired) return null;

        return FinalizePlan(method, paramPlans, returnsPointer);
    }

    private PairedMethodPlan FinalizePlan(
        IMethodSymbol method,
        List<PairedParamPlan> paramPlans,
        bool returnsPointer)
    {
        var declaration = method.DeclaringSyntaxReferences.First().GetSyntax();

        // 生成确定且成员内唯一的形参名。
        var memberTokens = new HashSet<string>(
            declaration.DescendantTokens().Select(t => t.ValueText));
        var used = new HashSet<string>(method.Parameters.Select(p => p.Name));

        var finalParams = new List<PairedParamPlan>();
        foreach (var plan in paramPlans)
        {
            if (plan.Kind == PairedParamKind.PlainPointer)
            {
                var indexName = MakeUnique(plan.Symbol!.Name + "Index", memberTokens, used);
                finalParams.Add(plan with { ArrayName = plan.Symbol.Name, IndexName = indexName });
            }
            else if (plan.Kind == PairedParamKind.HolderPointer)
            {
                var arrayName = MakeUnique(plan.Symbol!.Name + "Array", memberTokens, used);
                finalParams.Add(plan with { ArrayName = arrayName, IndexName = string.Empty });
            }
            else
            {
                finalParams.Add(plan);
            }
        }

        var returnElement = returnsPointer
            ? ((IPointerTypeSymbol)method.ReturnType).PointedAtType
            : null;

        return new PairedMethodPlan
        {
            Method = method,
            ReturnsPointer = returnsPointer,
            ReturnElement = returnElement,
            Parameters = finalParams,
        };
    }

    private static List<PairedParamPlan> BuildParamPlans(IMethodSymbol method)
    {
        var paramPlans = new List<PairedParamPlan>();
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type is IPointerTypeSymbol pointer)
            {
                paramPlans.Add(new PairedParamPlan
                {
                    Kind = PairedParamKind.PlainPointer,
                    Symbol = parameter,
                    Element = pointer.PointedAtType,
                });
            }
            else if (parameter.Type is INamedTypeSymbol named
                     && IsByRefHolder(named, out var holderElement))
            {
                paramPlans.Add(new PairedParamPlan
                {
                    Kind = PairedParamKind.HolderPointer,
                    Symbol = parameter,
                    Element = holderElement!,
                });
            }
            else
            {
                paramPlans.Add(new PairedParamPlan
                {
                    Kind = PairedParamKind.None,
                    Symbol = parameter,
                    Element = null,
                });
            }
        }

        return paramPlans;
    }

    /// <summary>
    /// 信号③④（<b>仅 fixed 块内</b>）：指针局部初值根植于指针形参，或指针
    /// 加减/比较/相减以指针形参为根。不进入嵌套局部函数/lambda 作用域。
    /// </summary>
    private static bool BodyHasFixedCursorSignal(
        SyntaxNode declaration,
        SemanticModel model,
        List<PairedParamPlan> paramPlans)
    {
        var pointerParams = new HashSet<ISymbol>(
            paramPlans.Where(p => p is { Kind: PairedParamKind.PlainPointer })
                .Select(p => p!.Symbol!),
            SymbolEqualityComparer.Default);
        if (pointerParams.Count == 0) return false;

        var nestedScopes = NestedScopes(declaration);

        bool IsInsideNestedScope(SyntaxNode node) =>
            nestedScopes.Any(n => n != node && n.Contains(node));

        bool InsideFixed(SyntaxNode node) =>
            node.Ancestors().TakeWhile(a => a != declaration)
                .Any(a => a.IsKind(SyntaxKind.FixedStatement));

        foreach (var declarator in declaration.DescendantNodes()
                     .OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() == null)
                continue;
            if (IsInsideNestedScope(declarator) || !InsideFixed(declarator)) continue;
            if (declarator.Initializer?.Value is not { } initializer) continue;

            var declared = model.GetDeclaredSymbol(declarator) as ILocalSymbol;
            if (declared?.Type is not IPointerTypeSymbol) continue;

            if (CollectRootSymbols(initializer, model).Any(pointerParams.Contains))
                return true;
        }

        foreach (var binary in declaration.DescendantNodes()
                     .OfType<BinaryExpressionSyntax>()
                     .Where(b => b.Kind() is SyntaxKind.AddExpression
                         or SyntaxKind.SubtractExpression
                         or SyntaxKind.LessThanExpression
                         or SyntaxKind.GreaterThanExpression
                         or SyntaxKind.LessThanOrEqualExpression
                         or SyntaxKind.GreaterThanOrEqualExpression
                         or SyntaxKind.EqualsExpression
                         or SyntaxKind.NotEqualsExpression))
        {
            if (IsInsideNestedScope(binary) || !InsideFixed(binary)) continue;
            if (model.GetTypeInfo(binary.Left).Type is not IPointerTypeSymbol
                && model.GetTypeInfo(binary.Right).Type is not IPointerTypeSymbol)
            {
                continue;
            }

            if (CollectRootSymbols(binary, model).Any(pointerParams.Contains))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 信号⑤：体内（非嵌套作用域）出现可证的 <c>ByRef&lt;T*&gt;.Create(...)</c>
    /// 提升——双 lambda 访问器形态（本族 C01 提升）或「数组+索引」形态。
    /// 裸槽工厂 Create()/Create(T) 的指针槽不可证，不构成配对信号。
    /// </summary>
    private static bool BodyHasHolderAccessor(
        SyntaxNode declaration, SemanticModel model)
    {
        var nestedScopes = NestedScopes(declaration);

        foreach (var invocation in declaration.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (nestedScopes.Any(n => n != invocation && n.Contains(invocation)))
                continue;

            if (!GetInvocationMethods(model, invocation).Any(IsHolderCreateSymbol))
                continue;

            var args = invocation.ArgumentList.Arguments;
            if (args.Count == 2
                && args.All(a => a.Expression is AnonymousFunctionExpressionSyntax))
            {
                return true;
            }

            if (args.Count == 2
                && model.GetTypeInfo(args[0].Expression).Type is IArrayTypeSymbol)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 信号⑥：把本方法的指针形参（含其偏移表达式）作为指针实参传给另一个已
    /// 配对方法。仅当实参根植于<b>本方法指针形参</b>时才要求配对——根植于
    /// fixed/局部游标的调用由 R3 降级表承载，不向上传播。
    /// </summary>
    private bool BodyForwardsToPairedMethod(
        SyntaxNode declaration,
        SemanticModel model,
        List<PairedParamPlan> paramPlans)
    {
        var pointerParams = new HashSet<ISymbol>(
            paramPlans.Where(p => p is { Kind: PairedParamKind.PlainPointer })
                .Select(p => p!.Symbol!),
            SymbolEqualityComparer.Default);
        if (pointerParams.Count == 0) return false;

        var nestedScopes = NestedScopes(declaration);

        foreach (var invocation in declaration.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (nestedScopes.Any(n => n != invocation && n.Contains(invocation)))
                continue;

            var calleePlans = GetInvocationMethods(model, invocation)
                .Select(GetPlan)
                .Where(p => p != null)
                .ToList();
            if (calleePlans.Count == 0) continue;

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (model.GetTypeInfo(argument.Expression).Type is not IPointerTypeSymbol)
                    continue;

                if (CollectRootSymbols(argument.Expression, model).Any(pointerParams.Contains))
                    return true;
            }
        }

        return false;
    }

    private static List<SyntaxNode> NestedScopes(SyntaxNode declaration) =>
        declaration.DescendantNodes().Where(n =>
            n is LocalFunctionStatementSyntax
                or SimpleLambdaExpressionSyntax
                or ParenthesizedLambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax).ToList();

    /// <summary>
    /// 取调用表达式绑定的方法符号：正常符号 + WrongArity 等候选形态
    /// （probe 实测显式泛型 <c>ByRef&lt;T*&gt;.Create&lt;T*&gt;</c> 落在候选）。
    /// </summary>
    private static IEnumerable<IMethodSymbol> GetInvocationMethods(
        SemanticModel model, InvocationExpressionSyntax invocation)
    {
        var info = model.GetSymbolInfo(invocation);
        if (info.Symbol is IMethodSymbol direct)
            yield return direct;

        foreach (var candidate in info.CandidateSymbols)
        {
            if (candidate is IMethodSymbol method)
                yield return method;
        }
    }

    private static IEnumerable<ISymbol> CollectRootSymbols(
        SyntaxNode node, SemanticModel model)
    {
        foreach (var identifier in node.DescendantNodesAndSelf()
                     .OfType<ExpressionSyntax>()
                     .Where(e => e is IdentifierNameSyntax or MemberAccessExpressionSyntax))
        {
            if (model.GetSymbolInfo(identifier).Symbol is { } symbol)
                yield return symbol;
        }
    }

    private static string MakeUnique(
        string candidate, HashSet<string> memberTokens, HashSet<string> used)
    {
        var result = candidate;
        var counter = 2;
        while (used.Contains(result) || memberTokens.Contains(result))
        {
            result = candidate + counter;
            counter++;
        }

        used.Add(result);
        memberTokens.Add(result);
        return result;
    }

    /// <summary>类型是否为 <c>Cs2jByRef.ByRef&lt;T*&gt;</c>，命中返回指针元素。</summary>
    public static bool IsByRefHolder(ITypeSymbol? type, out ITypeSymbol? element)
    {
        element = null;
        if (type is INamedTypeSymbol named
            && named is { IsGenericType: true, Name: "ByRef" }
            && named.ContainingNamespace?.ToDisplayString() == "Cs2jByRef"
            && named.TypeArguments.Length == 1
            && named.TypeArguments[0] is IPointerTypeSymbol pointer)
        {
            element = pointer.PointedAtType;
            return true;
        }

        return false;
    }

    /// <summary>符号是否为 holder 工厂 <c>ByRef&lt;T*&gt;.Create(...)</c>。</summary>
    public static bool IsHolderCreateSymbol(ISymbol? symbol)
        => symbol is IMethodSymbol method
           && method.Name == "Create"
           && IsByRefHolder(method.ContainingType, out _);

    /// <summary>指针元素是否在签名白名单（与 R5 的 12 基元 SpecialType 一致）。</summary>
    public static bool IsWhitelistElement(ITypeSymbol? element)
    {
        if (element == null) return false;
        if (element is ITypeParameterSymbol or IPointerTypeSymbol
            or IFunctionPointerTypeSymbol)
        {
            return false;
        }

        if (element.SpecialType == SpecialType.System_Void) return false;
        if (element.IsReferenceType || element is IArrayTypeSymbol) return false;

        return element.SpecialType is SpecialType.System_Boolean
            or SpecialType.System_Char
            or SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Single
            or SpecialType.System_Double;
    }
}

internal enum PairedParamKind
{
    /// <summary>非指针形参：原样透传。</summary>
    None,

    /// <summary>白名单指针形参：降级为「数组 + int 索引」两个形参。</summary>
    PlainPointer,

    /// <summary>holder 指针形参 ByRef&lt;T*&gt;：降级为 ByRef&lt;int&gt; + 伴生数组形参。</summary>
    HolderPointer,
}

internal sealed record PairedParamPlan
{
    public required PairedParamKind Kind { get; init; }
    public required IParameterSymbol? Symbol { get; init; }
    public required ITypeSymbol? Element { get; init; }

    /// <summary>PlainPointer：数组形参名（原形参名）；HolderPointer：伴生数组形参名。</summary>
    public string ArrayName { get; init; } = string.Empty;

    /// <summary>PlainPointer：插入的 int 索引形参名。</summary>
    public string IndexName { get; init; } = string.Empty;
}

internal sealed record PairedMethodPlan
{
    public required IMethodSymbol Method { get; init; }
    public required bool ReturnsPointer { get; init; }
    public required ITypeSymbol? ReturnElement { get; init; }

    /// <summary>与原方法形参逐位对齐的计划（一个原参数一个条目）。</summary>
    public required List<PairedParamPlan> Parameters { get; init; }
}
