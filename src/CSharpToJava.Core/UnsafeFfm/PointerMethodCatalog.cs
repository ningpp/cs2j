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
/// <item>③④ 方法体（含 fixed 块与裸 unsafe 域，排除嵌套局部函数/lambda）
/// 存在指针局部/fixed 变量以<b>非裸</b>初值根植于指针形参（<c>T* q = p±n</c>
/// 等偏移游标；裸别名 <c>T* q = p;</c> 走 array-only，不触发）；或指针
/// 加减/比较/相减的根（裸别名局部经闭包追溯到形参根）出现两个不同本方法
/// 指针形参（跨形参入参索引运算，array-only 形参无法表达），或出现一个
/// 形参与一个同命名族 fixed 游标（<c>pDst - pDstBegin</c>）；纯指针形参的
/// 跨形参末端指针对（<c>p &lt; pEnd</c>）同属此规则；</item>
/// <item>⑤ 方法体内出现 <c>ByRef&lt;T*&gt;.Create(...)</c> 访问器提升
/// （本方法指针游标经 holder 传入配对被调方）；</item>
/// <item>⑥ 方法把自己的指针形参（含其 ±n 偏移）作为指针实参传给另一个已配对
/// 方法（调用约定沿调用图传播；指针实参仅根植于 fixed/局部游标时不要求配对，
/// R3 降级表即可承载）；</item>
/// <item>⑦ 调用图强制闭包：本方法把<b>可证域</b>指针实参传给源码内另一个
/// 含白名单指针形参的方法时，被调方必须按配对约定降级——否则数组-only 被调
/// 方无从表达非零游标偏移。可证根中 fixed 变量、其别名局部与 GCHandle.Pinned
/// 固定数组局部始终参与；指针形参根仅在本方法自身配对（或被闭包强制）时
/// 参与，array-only 纯下标方法的零偏移裸形参外发不产生强制边。闭包沿调用边
/// 单调传播，不可证实参在 R6 展开点仍保留 UF020。</item>
/// <item>⑧ 方法把携带非零偏移的本方法指针形参（<c>p±n</c> 等非裸指针
/// 表达式）作为指针实参外发给含白名单指针形参的方法（含元数据/extern，如
/// <c>Encoding.UTF8.GetBytes(pInput + next, ...)</c>）：array-only 的
/// <c>T[] p</c> 无法承载偏移实参，须配对为 <c>pIndex + n</c>。</item>
/// </list>
/// 配对方法中<b>每个</b>白名单指针形参统一降级为 <c>T[] p, int pIndex</c>；
/// holder 形参降级为 <c>ByRef&lt;int&gt; h, T[] hArray</c>。入参同源关系是
/// 指针方法的既有入口契约（形参解引用本就无条件信任调用方数组）：同命名族
/// （去 EndPos/End/Begin 后缀，如 p/pEnd、pSrcBegin/pSrcEnd）的形参索引、
/// 以及形参索引与同方法同命名族 fixed 索引（形参 pDst 对 fixed pDstBegin）
/// 间的比较/相减按 int 索引运算归一；异族形参（如 a 对 b、pChars 对
/// pBytes）与异源 fixed 游标的跨槽运算仍保守 UF020。
/// </para>
/// <para>
/// 不配对的形态：抽象/extern/virtual/override/接口/委托/元数据方法（声明不在
/// 本编译源码中，无法一致改写所有实现/调用点）、含非白名单指针签名的方法
/// （交 R5 既有 UF021 路径）、不持有 fixed 缓冲、不在入参指针上做比较/算术、
/// 也不经任何可证域调用边接收指针游标的纯下标指针形参方法（交既有 R5
/// array-only/R6 同源判定路径）。
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

    /// <summary>信号⑦ 调用图闭包中被可证域指针实参强制配对的方法。</summary>
    private readonly HashSet<IMethodSymbol> _forced =
        new(SymbolEqualityComparer.Default);

    /// <summary>
    /// UF040 降级域：经 <see cref="StackAllocPointerSafety"/> 判定可降级的
    /// 裸指针 stackalloc 局部（信号⑦可证根的额外来源；含候选根，逐文件
    /// catalog 随 RewriteFile 存活，不可降级站点所在文件整文回滚不落盘）。
    /// </summary>
    private readonly HashSet<ISymbol> _stackAllocCursorRoots =
        new(SymbolEqualityComparer.Default);

    /// <summary>
    /// 注册可降级裸指针 stackalloc 局部为信号⑦可证游标根：本作用域把该
    /// 游标外发给源码内白名单指针形参方法时，被调方必须按配对约定降级，
    /// 调用点才能把「伴生数组 + 0 索引」展开为配对实参。
    /// </summary>
    public void RegisterStackAllocCursorRoots(IEnumerable<ISymbol> locals)
    {
        foreach (var local in locals)
            _stackAllocCursorRoots.Add(local);
    }

    /// <summary>
    /// 重解释存储约束（信号⑦扩展）：被调方法某指针形参位实际接收的存储元素
    /// （调用实参为 <c>(T2*)inner</c>、inner 为异类 T1* 时记 T1）。同一形参位
    /// 多条边存储不一致即冲突。键内字典的键为形参位置。
    /// </summary>
    private readonly Dictionary<IMethodSymbol, Dictionary<int, ITypeSymbol>>
        _reinterpretStorage = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// 重解释冲突方法：存储约束矛盾，或方法体对重解释形参存在只读打包解引用
    /// 之外的使用（写入/自增/外发/下标/比较等）。其配对计划恒为 null。
    /// </summary>
    private readonly HashSet<IMethodSymbol> _reinterpretConflicts =
        new(SymbolEqualityComparer.Default);

    /// <summary>
    /// 整文回滚（不落盘）文件的语法树集合：其内声明的方法不参与配对闭包、
    /// 计划恒为 null；活动文件发往其内方法的强制边一律忽略（调用点按不可证
    /// 处理，调用方随之回滚），保证落盘产物跨文件签名一致。
    /// </summary>
    private readonly HashSet<SyntaxTree> _disabledTrees = new();

    /// <summary>
    /// 新一轮编译级闭包分析：清空逐轮缓存与强制/重解释结论及 stackalloc
    /// 种子，登记本轮的回滚文件集合。compilation 不变（同一进程内语义符号
    /// 跨轮可比）。
    /// </summary>
    public void ResetRound(IEnumerable<SyntaxTree>? disabledTrees = null)
    {
        _cache.Clear();
        _forced.Clear();
        _inProgress.Clear();
        _stackAllocCursorRoots.Clear();
        _reinterpretStorage.Clear();
        _reinterpretConflicts.Clear();
        _annotationSymbols.Clear();
        _annotationSequence = 0;
        _disabledTrees.Clear();
        if (disabledTrees != null)
        {
            foreach (var tree in disabledTrees)
                _disabledTrees.Add(tree);
        }
    }

    private bool IsTreeDisabled(IMethodSymbol method)
    {
        var reference = method.DeclaringSyntaxReferences.FirstOrDefault();
        return reference != null && _disabledTrees.Contains(reference.SyntaxTree);
    }

    /// <summary>
    /// 编译级信号⑦闭包驱动：在活动（本轮不回滚的）语法树范围内，对全部含
    /// 白名单指针形参的源码方法反复求计划，直到强制集与重解释冲突集不动点。
    /// 必须在任何文件改写前完成，保证「声明点 ↔ 全部调用点」跨文件一致。
    /// </summary>
    public void ComputeClosure(IEnumerable<SyntaxTree> activeTrees)
    {
        var candidates = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in activeTrees)
        {
            if (_disabledTrees.Contains(tree)) continue;

            var treeModel = _compilation.GetSemanticModel(tree);
            SyntaxNode? root;
            try
            {
                root = tree.GetRoot();
            }
            catch
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().Where(n =>
                         n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax))
            {
                if (treeModel.GetDeclaredSymbol(declaration) is not IMethodSymbol method)
                    continue;
                if (method.Parameters.Any(p =>
                        p.Type is IPointerTypeSymbol pointer
                        && IsWhitelistElement(pointer.PointedAtType)))
                {
                    candidates.Add(method);
                }
            }
        }

        var forcedCount = -1;
        var conflictCount = -1;
        while (forcedCount != _forced.Count || conflictCount != _reinterpretConflicts.Count)
        {
            forcedCount = _forced.Count;
            conflictCount = _reinterpretConflicts.Count;
            foreach (var method in candidates)
                GetPlan(method);
        }
    }

    public static bool TryPrimitiveSize(ITypeSymbol? element, out int size)
    {
        switch (element?.SpecialType)
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

    /// <summary>
    /// 判定解引用操作数是否为「重解释存储形参上的只读打包读」：
    /// <c>*p</c>、<c>*(p ± k)</c> 或 <c>*(T3*)(p ± k)</c>，基标 p 是
    /// <paramref name="plan"/> 中带 <see cref="PairedParamPlan.StorageElement"/>
    /// 的形参，k 为编译期常量，且声明/目标元素尺寸可被存储元素整除。
    /// 供 R3 前置门放行（真正的索引化与小端拼包在 R6
    /// TryBuildReinterpretRead 完成）。
    /// </summary>
    public static bool MatchesReinterpretStorageRead(
        ExpressionSyntax rawOperand,
        SemanticModel model,
        OriginalSyntaxResolver? resolver,
        PairedMethodPlan? plan)
    {
        if (plan == null) return false;

        ExpressionSyntax? ToModel(ExpressionSyntax node)
            => node.SyntaxTree == model.SyntaxTree
                ? node
                : resolver?.Resolve(node) as ExpressionSyntax;

        var address = UnwrapParentheses(rawOperand);
        ITypeSymbol? targetElement = null;
        if (address is CastExpressionSyntax addressCast
            && addressCast.Type is PointerTypeSyntax)
        {
            if (ToModel(addressCast) is { } modelCast
                && model.GetTypeInfo(modelCast).Type is IPointerTypeSymbol castPointer)
            {
                targetElement = castPointer.PointedAtType;
            }

            address = UnwrapParentheses(addressCast.Expression);
        }

        ExpressionSyntax baseExpression;
        if (address is IdentifierNameSyntax)
        {
            baseExpression = address;
        }
        else if (address is BinaryExpressionSyntax binary
                 && (binary.IsKind(SyntaxKind.AddExpression)
                     || binary.IsKind(SyntaxKind.SubtractExpression))
                 && (UnwrapParentheses(binary.Left) is IdentifierNameSyntax
                     || UnwrapParentheses(binary.Right) is IdentifierNameSyntax))
        {
            var modelBinary = ToModel(binary) ?? binary;
            if (model.GetTypeInfo(modelBinary).Type is not IPointerTypeSymbol)
                return false;

            IdentifierNameSyntax sideIdentifier;
            ExpressionSyntax constantSide;
            if (UnwrapParentheses(binary.Left) is IdentifierNameSyntax leftIdentifier)
            {
                sideIdentifier = leftIdentifier;
                constantSide = binary.Right;
            }
            else
            {
                sideIdentifier = (IdentifierNameSyntax)UnwrapParentheses(binary.Right);
                constantSide = binary.Left;
            }

            var constantOriginal = ToModel(constantSide) ?? constantSide;
            if (model.GetConstantValue(constantOriginal).Value is not int)
                return false;

            baseExpression = sideIdentifier;
        }
        else
        {
            return false;
        }

        var baseSymbol = ToModel(baseExpression) is { } modelBase
            ? model.GetSymbolInfo(modelBase).Symbol
            : null;

        var slot = plan.Parameters.FirstOrDefault(p =>
            p.Kind == PairedParamKind.PlainPointer
            && p.StorageElement != null
            && SymbolEqualityComparer.Default.Equals(p.Symbol, baseSymbol));
        if (slot is not { Symbol.Type: IPointerTypeSymbol slotPointer,
                StorageElement: { } storage })
        {
            return false;
        }

        var declaredElement = slotPointer.PointedAtType;
        var readElement = targetElement ?? declaredElement;
        if (!TryPrimitiveSize(declaredElement, out var declaredSize)
            || !TryPrimitiveSize(storage, out var storageSize)
            || !TryPrimitiveSize(readElement, out var targetSize)
            || declaredSize % storageSize != 0
            || targetSize % storageSize != 0)
        {
            return false;
        }

        // 形态与尺寸与 IsReinterpretBodyEligible / R6 TryBuildReinterpretRead
        // 接受域一致：偏移为任意编译期常量（负偏移在 R6 生成 index - k，
        // 入口索引运行时才可知，前置门不做越界判决）；plan 存在本身已保证
        // 体内该形参全部使用都是只读前缀解引用。
        return true;
    }

    /// <summary>
    /// 判定指针强转是否为「重解释存储实参」：<c>(T2*)(p ± k)</c> 作为位置实参
    /// 传给源码内配对方法，且对应形参槽带
    /// <see cref="PairedParamPlan.StorageElement"/> 约束（实存 T1[]）。
    /// R3/R6 的 cast 前置门据此放行，调用点由 R6 ExpandPairedInvocation
    /// 剥除强转、按「数组 + 存储单位索引」展开；不可证根由 R6 落 UF020。
    /// </summary>
    public bool IsReinterpretArgumentCast(
        CastExpressionSyntax cast,
        SemanticModel model,
        OriginalSyntaxResolver? resolver)
    {
        if (cast.Type is not PointerTypeSyntax) return false;

        SyntaxNode? current = cast;
        while (current.Parent is ParenthesizedExpressionSyntax parenthesized
               && parenthesized.Expression == current)
        {
            current = parenthesized;
        }

        if (current.Parent is not ArgumentSyntax argument
            || argument.Expression != (ExpressionSyntax)current
            || argument.NameColon != null)
        {
            return false;
        }

        if (argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        var parameterIndex = argumentList.Arguments.IndexOf(argument);
        if (parameterIndex < 0) return false;

        var modelInvocation = invocation.SyntaxTree == model.SyntaxTree
            ? invocation
            : resolver?.Resolve(invocation) as InvocationExpressionSyntax;
        if (modelInvocation == null) return false;

        var inner = UnwrapParentheses(cast.Expression);
        var modelInner = inner.SyntaxTree == model.SyntaxTree
            ? inner
            : resolver?.Resolve(inner) as ExpressionSyntax;
        if (model.GetTypeInfo(modelInner ?? inner).Type is not IPointerTypeSymbol innerPointer
            || !IsWhitelistElement(innerPointer.PointedAtType))
        {
            return false;
        }

        var info = model.GetSymbolInfo(modelInvocation);
        var methods = new List<IMethodSymbol>();
        if (info.Symbol is IMethodSymbol direct) methods.Add(direct);
        foreach (var candidate in info.CandidateSymbols)
        {
            if (candidate is IMethodSymbol candidateMethod) methods.Add(candidateMethod);
        }

        foreach (var method in methods)
        {
            if (GetPlan(method) is not { } plan) continue;
            if (parameterIndex >= plan.Parameters.Count) continue;
            var slot = plan.Parameters[parameterIndex];
            if (slot.Kind == PairedParamKind.PlainPointer && slot.StorageElement != null)
                return true;
        }

        return false;
    }

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
        if (IsTreeDisabled(method)) return null;
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
                paired = _forced.Contains(method)
                    || BodyHasCursorSignal(declaration, treeModel, paramPlans)
                    || BodyHasHolderAccessor(declaration, treeModel)
                    || BodyForwardsToPairedMethod(declaration, treeModel, paramPlans)
                    || BodyForwardsParamOffsetArgument(declaration, treeModel, paramPlans);
            }
            finally
            {
                _inProgress.Remove(method);
            }
        }

        // 信号⑦：fixed 变量/别名/Pinned 句柄根始终参与外发边强制；指针形参
        // 根仅在本方法自身配对时参与——array-only 纯下标方法的零偏移裸形参
        // 外发不需要被调方配对（被调方接收整组数组），否则会错误改写其既有
        // 成功产物中对回滚文件方法的调用形态。
        ForcePairedCallees(declaration, treeModel, method, paired);

        // 强制标记可能由分析中触发的递归闭包回灌本方法。
        if (!paired && hasPointerParam && _forced.Contains(method))
            paired = true;

        if (!paired) return null;

        // 重解释存储约束（(T2*)(T1* p) 调用族）：把对应指针形参的存储元素
        // 钉为 T1 并校验体内使用全部是只读打包解引用；冲突/越界使用即不可
        // 配对（计划 null，调用点落 UF020/UF021）。
        if (!TryApplyReinterpretStorage(method, paramPlans, treeModel, declaration))
            return null;

        return FinalizePlan(method, paramPlans, returnsPointer);
    }

    /// <summary>
    /// 套用信号⑦记录的重解释存储约束：异类存储（声明 T2*、实存 T1[]）写入
    /// 形参计划，并逐形参校验方法体内对该形参的所有使用都落在
    /// <c>*p</c> / <c>*(p ± k)</c> / <c>*(T3*)(p ± k)</c> 只读解引用链内
    /// （k 为编译期常量；解引用不得为赋值目标）。任一条件不满足即冲突。
    /// </summary>
    private bool TryApplyReinterpretStorage(
        IMethodSymbol method,
        List<PairedParamPlan> paramPlans,
        SemanticModel model,
        SyntaxNode declaration)
    {
        if (_reinterpretConflicts.Contains(method))
            return false;

        if (!_reinterpretStorage.TryGetValue(method, out var constraints))
            return true;

        foreach (var pair in constraints)
        {
            var index = pair.Key;
            var storage = pair.Value;
            if ((uint)index >= (uint)paramPlans.Count)
            {
                _reinterpretConflicts.Add(method);
                return false;
            }

            var plan = paramPlans[index];
            if (plan is not { Kind: PairedParamKind.PlainPointer, Symbol: { } parameter }
                || parameter.Type is not IPointerTypeSymbol declaredPointer)
            {
                _reinterpretConflicts.Add(method);
                return false;
            }

            var declaredElement = declaredPointer.PointedAtType;
            if (SymbolEqualityComparer.Default.Equals(storage, declaredElement))
                continue;

            if (!IsWhitelistElement(storage)
                || !TryPrimitiveSize(declaredElement, out var declaredSize)
                || !TryPrimitiveSize(storage, out var storageSize)
                || declaredSize < storageSize
                || declaredSize % storageSize != 0)
            {
                _reinterpretConflicts.Add(method);
                return false;
            }

            paramPlans[index] = plan with { StorageElement = storage };
        }

        foreach (var plan in paramPlans)
        {
            if (plan.StorageElement == null || plan.Symbol == null)
                continue;
            if (!IsReinterpretBodyEligible(declaration, model, plan.Symbol))
            {
                _reinterpretConflicts.Add(method);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 重解释形参体内使用合法性：形参标识符只允许出现在
    /// <c>p</c>/<c>p ± const</c>（可再经一层指针强转）→ 前缀解引用链内，
    /// 且解引用为读取；形参出现在实参、返回、比较、自增、下标、裸赋值等
    /// 其它位置均判不可降级。
    /// </summary>
    private static bool IsReinterpretBodyEligible(
        SyntaxNode declaration,
        SemanticModel model,
        IParameterSymbol parameter)
    {
        var nestedScopes = NestedScopes(declaration);

        foreach (var identifier in declaration.DescendantNodes()
                     .OfType<IdentifierNameSyntax>())
        {
            if (nestedScopes.Any(n => n != identifier && n.Contains(identifier)))
                continue;
            if (!SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(identifier).Symbol, parameter))
            {
                continue;
            }

            ExpressionSyntax cursor = identifier;
            SyntaxNode? parent = cursor.Parent;

            // p ± const：仅一层，另一侧必须是编译期常量。
            if (parent is BinaryExpressionSyntax binary
                && (binary.IsKind(SyntaxKind.AddExpression)
                    || binary.IsKind(SyntaxKind.SubtractExpression))
                && (binary.Left == cursor || binary.Right == cursor))
            {
                var other = binary.Left == cursor ? binary.Right : binary.Left;
                if (model.GetTypeInfo(binary).Type is not IPointerTypeSymbol
                    || model.GetConstantValue(other).Value is not int)
                {
                    return false;
                }

                cursor = binary;
                parent = binary.Parent;
            }

            while (parent is ParenthesizedExpressionSyntax parenthesized
                   && parenthesized.Expression == cursor)
            {
                cursor = parenthesized;
                parent = parenthesized.Parent;
            }

            // 可选一层 (T3*) 重解释强转；目标元素必须在白名单内。
            if (parent is CastExpressionSyntax cast && cast.Expression == cursor)
            {
                if (model.GetTypeInfo(cast).Type is not IPointerTypeSymbol castPointer
                    || !IsWhitelistElement(castPointer.PointedAtType)
                    || !TryPrimitiveSize(castPointer.PointedAtType, out var targetSize)
                    || (TryPrimitiveSize(
                            ((IPointerTypeSymbol)parameter.Type).PointedAtType,
                            out var paramSize)
                        && targetSize > paramSize))
                {
                    return false;
                }

                cursor = cast;
                parent = cast.Parent;
                while (parent is ParenthesizedExpressionSyntax wrapped
                       && wrapped.Expression == cursor)
                {
                    cursor = wrapped;
                    parent = wrapped.Parent;
                }
            }

            // 必须落到前缀解引用（读取）。
            if (parent is not PrefixUnaryExpressionSyntax
                    { RawKind: (int)SyntaxKind.PointerIndirectionExpression } dereference
                || dereference.Operand != cursor)
            {
                return false;
            }

            if (dereference.Parent is AssignmentExpressionSyntax assignment
                && assignment.Left == dereference)
            {
                return false;
            }
        }

        return true;
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

        // 游标命名族分组（入口契约的统一命名约定，如 p/pEnd、
        // pChars/pCharsEndPos、pSrcBegin/pSrcEnd）：形参名去 EndPos/End/Begin
        // 后缀同族时，全族共享一个源槽（族内最早声明者为代表）——其索引处于
        // 同一地址空间，比较/相减可按同源归一。无族成员的独立指针形参各自成槽，
        // 跨槽比较/相减仍保守 UF020。同族槽键同时供 R3 fixed 帧按
        // 「形参 pDst ↔ fixed pDstBegin」约定继承（见 FixedStatementRewriter）。
        var familyMembers = new Dictionary<string, List<int>>(
            StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < finalParams.Count; i++)
        {
            var plan = finalParams[i];
            if (plan.Kind != PairedParamKind.PlainPointer || plan.Symbol == null)
                continue;

            var family = CursorFamily(plan.Symbol.Name);
            if (!familyMembers.TryGetValue(family, out var members))
            {
                members = new List<int>();
                familyMembers[family] = members;
            }

            members.Add(i);
        }

        foreach (var members in familyMembers.Values)
        {
            if (members.Count < 2) continue;
            var representative = finalParams[members[0]].Symbol!;
            foreach (var index in members)
            {
                if (SymbolEqualityComparer.Default.Equals(
                        finalParams[index].Symbol, representative))
                {
                    continue;
                }

                finalParams[index] =
                    finalParams[index] with { SlotBaseSymbol = representative };
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

    /// <summary>
    /// 游标命名族的族键（入口契约约定，非逐站点白名单）：形参/fixed 游标名
    /// （大小写不敏感）以 <c>EndPos</c>/<c>End</c>/<c>Begin</c> 结尾时去单个
    /// 后缀取词干（EndPos 优先于 End），如 <c>pSrcBegin</c>/<c>pSrcEnd</c>
    /// 同属 <c>pSrc</c> 族、形参 <c>pDst</c> 与同方法 fixed <c>pDstBegin</c>
    /// 同属 <c>pDst</c> 族；不带后缀或去后缀为空（如名即 end/begin）时族键
    /// 为全名，各自独立。指针关系比较/相减按无符号地址，不要求同元素类型。
    /// </summary>
    public static string CursorFamily(string name)
    {
        if (name.Length > "EndPos".Length
            && name.EndsWith("EndPos", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^"EndPos".Length];
        }

        if (name.Length > "End".Length
            && name.EndsWith("End", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^"End".Length];
        }

        if (name.Length > "Begin".Length
            && name.EndsWith("Begin", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^"Begin".Length];
        }

        return name;
    }

    /// <summary>
    /// 符号的稳定源槽键：字段用含类型的显示名（避免不同接收者同名字段混淆），
    /// 局部/形参用名称（同一方法作用域内唯一）。R5 flow 登记与 R3 fixed
    /// 真源溯源共用，保证跨 pass 判定一致。
    /// </summary>
    public static string SymbolSlot(ISymbol symbol)
        => symbol is IFieldSymbol field
            ? "field:" + field.ToDisplayString()
            : "sym:" + symbol.Name;

    /// <summary>
    /// 取初始式真源的源槽键：标识符/成员访问绑定到局部/形参/字段符号时
    /// 返回其稳定键；其它表达式形态（调用、算术等）返回 false，由调用方按
    /// 独立伴生槽保守处理。
    /// </summary>
    public static bool TryGetExpressionSlot(
        ExpressionSyntax? raw,
        SemanticModel model,
        OriginalSyntaxResolver? resolver,
        out string slot)
    {
        slot = string.Empty;
        if (raw == null)
            return false;

        var expression = UnwrapParentheses(raw);
        ExpressionSyntax? original =
            expression.SyntaxTree == model.SyntaxTree
                ? expression
                : resolver?.Resolve(expression) as ExpressionSyntax;
        if (original == null)
            return false;

        if (model.GetSymbolInfo(original).Symbol is { } symbol
            && symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol)
        {
            slot = SymbolSlot(symbol);
            return true;
        }

        return false;
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
    /// 信号③④（<b>整个方法体</b>，含 fixed 块与裸 unsafe 域），仅以下
    /// array-only 改写无法承载的形态才触发配对：
    /// <list type="bullet">
    /// <item>③ 指针局部/fixed 变量以<b>非裸</b>初值根植于指针形参
    /// （<c>T* q = p ± n</c> 算术/强转/调用等偏移游标——array-only 的
    /// <c>T[] q = p</c> 无法表达非零偏移）；裸标识符别名 <c>T* q = p;</c>
    /// 不直接触发，array-only 可归一为 <c>T[] q = p;</c>；</item>
    /// <item>④ 指针加减/比较/相减中出现 (a) <b>两个不同</b>的本方法指针形参
    /// （跨形参入参索引运算，如 <c>p &lt; pEnd</c>、<c>pEnd - p</c>；裸别名
    /// 局部经闭包追溯到其形参根，如 <c>char* pSrc = pSrcBegin; pSrc &lt;
    /// pSrcEnd</c>），或 (b) 一个指针形参与一个同命名族 fixed 游标（形参
    /// <c>pDst</c> 与 <c>fixed(char* pDstBegin = ...)</c> 间的 <c>pDst -
    /// pDstBegin</c>：入参指针进入本方法 fixed 缓冲的索引差，array-only
    /// 形参无从表达）。形参族与 fixed 族不同名（如 pSrc 形参对 pDstBegin
    /// fixed）不触发；末端形参是否同源由 R6 源槽门按命名族判定，此处只决定
    /// 配对。</item>
    /// </list>
    /// 不进入嵌套局部函数/lambda 作用域。
    /// </summary>
    private static bool BodyHasCursorSignal(
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

        foreach (var declarator in declaration.DescendantNodes()
                     .OfType<VariableDeclaratorSyntax>())
        {
            // 指针局部声明与 fixed 变量声明（R3 的 fixed 偏移路径同样依赖
            // 配对 int 索引承载 p±n）。
            var isLocalDeclarator =
                declarator.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() != null;
            var isFixedDeclarator =
                declarator.FirstAncestorOrSelf<FixedStatementSyntax>() != null;
            if (!isLocalDeclarator && !isFixedDeclarator)
                continue;
            if (IsInsideNestedScope(declarator)) continue;
            if (declarator.Initializer?.Value is not { } initializer) continue;

            var declared = model.GetDeclaredSymbol(declarator) as ILocalSymbol;
            if (declared?.Type is not IPointerTypeSymbol) continue;

            // 裸标识符别名 T* q = p;（可去括号）：array-only 形态 T[] q = p
            // 足以承载（fixed 同名路径亦然），不触发配对。
            if (UnwrapParentheses(initializer) is IdentifierNameSyntax)
                continue;

            if (CollectRootSymbols(initializer, model).Any(pointerParams.Contains))
                return true;
        }

        var closure = BuildLocalAliasClosure(declaration, model, IsInsideNestedScope);
        var fixedCursors = CollectFixedCursorSymbols(declaration, model, IsInsideNestedScope);

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
            if (IsInsideNestedScope(binary)) continue;
            if (model.GetTypeInfo(binary.Left).Type is not IPointerTypeSymbol
                && model.GetTypeInfo(binary.Right).Type is not IPointerTypeSymbol)
            {
                continue;
            }

            // 根经裸别名闭包展开：局部别名追溯到其初值根（形参/fixed），
            // 使 pSrc=pSrcBegin 后的 pSrc<pSrcEnd 识别为两个形参根。
            var roots = ExpandRoots(
                CollectRootSymbols(binary, model), closure);
            var binaryParamRoots = new HashSet<ISymbol>(
                roots.Where(pointerParams.Contains),
                SymbolEqualityComparer.Default);
            if (binaryParamRoots.Count >= 2)
                return true;

            // (b) 形参根与同命名族 fixed 游标根共同参与运算（pDst -
            // pDstBegin）：仅同族名放行配对，跨族名（pSrc 对 pDstBegin）
            // 不触发，比较/相减仍由 R6 源槽门保守裁决。
            if (binaryParamRoots.Count >= 1)
            {
                var binaryFixedRoots = new HashSet<ISymbol>(
                    roots.Where(fixedCursors.Contains),
                    SymbolEqualityComparer.Default);
                foreach (var parameterRoot in binaryParamRoots)
                {
                    var paramFamily = CursorFamily(parameterRoot.Name);
                    if (binaryFixedRoots.Any(fixedRoot =>
                            string.Equals(
                                CursorFamily(fixedRoot.Name),
                                paramFamily,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 收集本方法（非嵌套作用域）全部 fixed 游标符号。
    /// </summary>
    private static HashSet<ISymbol> CollectFixedCursorSymbols(
        SyntaxNode declaration,
        SemanticModel model,
        Func<SyntaxNode, bool> isInsideNestedScope)
    {
        var fixedCursors = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var fixedDeclarator in declaration.DescendantNodes()
                     .OfType<FixedStatementSyntax>()
                     .SelectMany(f => f.Declaration.Variables))
        {
            if (isInsideNestedScope(fixedDeclarator)) continue;
            if (model.GetDeclaredSymbol(fixedDeclarator) is { } fixedSymbol)
                fixedCursors.Add(fixedSymbol);
        }

        return fixedCursors;
    }

    /// <summary>
    /// 裸别名闭包：本方法（非嵌套作用域）指针局部符号 → 其声明初值中的直接
    /// 根符号集合（形参/fixed/其它指针局部）。仅收录有初值的指针局部；
    /// 供 <see cref="ExpandRoots"/> 把表达式中的局部根不动点追溯到形参/fixed。
    /// </summary>
    private static Dictionary<ISymbol, HashSet<ISymbol>> BuildLocalAliasClosure(
        SyntaxNode declaration,
        SemanticModel model,
        Func<SyntaxNode, bool> isInsideNestedScope)
    {
        var direct = new Dictionary<ISymbol, HashSet<ISymbol>>(
            SymbolEqualityComparer.Default);

        foreach (var declarator in declaration.DescendantNodes()
                     .OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() == null)
                continue;
            if (isInsideNestedScope(declarator)) continue;
            if (declarator.Initializer?.Value is not { } initializer) continue;

            if (model.GetDeclaredSymbol(declarator) is not ILocalSymbol
                { Type: IPointerTypeSymbol } local)
            {
                continue;
            }

            direct[local] = new HashSet<ISymbol>(
                CollectRootSymbols(initializer, model),
                SymbolEqualityComparer.Default);
        }

        // 不动点展开每个局部的形参/fixed 终根。
        var closure = new Dictionary<ISymbol, HashSet<ISymbol>>(
            SymbolEqualityComparer.Default);
        foreach (var local in direct.Keys)
            closure[local] = ExpandRoots(new[] { local }, direct);
        return closure;
    }

    /// <summary>
    /// 把种子根中的指针局部按别名闭包递归替换为其初值根；形参/fixed/未知
    /// 符号为终根。
    /// </summary>
    private static HashSet<ISymbol> ExpandRoots(
        IEnumerable<ISymbol> seeds,
        Dictionary<ISymbol, HashSet<ISymbol>> closure)
    {
        var result = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ISymbol>(seeds);
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        while (pending.Count > 0)
        {
            var root = pending.Pop();
            if (!seen.Add(root)) continue;
            if (closure.TryGetValue(root, out var nested))
            {
                foreach (var nestedRoot in nested)
                    pending.Push(nestedRoot);
            }
            else
            {
                result.Add(root);
            }
        }

        return result;
    }

    /// <summary>
    /// 信号⑦：扫描本方法的外发调用边，凡把<b>可证域</b>指针实参（根符号为
    /// 指针形参、fixed 变量、其别名局部或 Pinned 固定数组局部）传给源码内
    /// 含白名单指针形参的方法/局部函数，即把被调方加入强制配对闭包并即时
    /// 求其计划（沿调用图继续传播）。不可证实参不影响被调方配对状态——R6
    /// 展开点仍会对不可证形态保留 UF020。
    /// </summary>
    private void ForcePairedCallees(
        SyntaxNode declaration,
        SemanticModel model,
        IMethodSymbol currentMethod,
        bool includeParameterRoots)
    {
        var provableRoots = CollectProvableCursorRoots(
            declaration, model, includeParameterRoots);
        if (provableRoots.Count == 0) return;

        var nestedScopes = NestedScopes(declaration);

        foreach (var invocation in declaration.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (nestedScopes.Any(n => n != invocation && n.Contains(invocation)))
                continue;

            foreach (var callee in GetInvocationMethods(model, invocation))
            {
                if (SymbolEqualityComparer.Default.Equals(callee, currentMethod))
                    continue;
                if (!callee.Parameters.Any(p =>
                        p.Type is IPointerTypeSymbol pointer
                        && IsWhitelistElement(pointer.PointedAtType)))
                {
                    continue;
                }

                if (callee.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                    is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax))
                {
                    continue;
                }

                // 回滚文件中的被调方签名维持指针形态，强制边不成立：调用点按
                // 不可证外发处理（活动调用方随之回滚），避免跨文件签名错配。
                if (_disabledTrees.Contains(
                        callee.DeclaringSyntaxReferences[0].SyntaxTree))
                {
                    continue;
                }

                var constraintsChanged = false;
                var forcedByArg = false;
                var arguments = invocation.ArgumentList.Arguments;
                for (var i = 0; i < arguments.Count; i++)
                {
                    var argument = arguments[i];
                    if (model.GetTypeInfo(argument.Expression).Type
                        is not IPointerTypeSymbol)
                    {
                        continue;
                    }

                    if (!CollectRootSymbols(argument.Expression, model)
                            .Any(provableRoots.Contains))
                    {
                        continue;
                    }

                    forcedByArg = true;

                    // 记录该形参位的实际存储元素：(T2*)inner（inner 为异类
                    // T1*）→ T1；裸/同型实参 → 声明元素 T2。仅位置实参。
                    if (argument.NameColon == null
                        && i < callee.Parameters.Length
                        && callee.Parameters[i].Type is IPointerTypeSymbol slotPointer)
                    {
                        var storage = ResolveArgumentStorageElement(
                            argument.Expression, model, slotPointer.PointedAtType);
                        if (storage != null
                            && RecordStorageConstraint(callee, i, storage))
                        {
                            constraintsChanged = true;
                        }
                    }
                }

                if (!forcedByArg) continue;

                if (_forced.Add(callee) || constraintsChanged)
                {
                    _cache.Remove(callee);
                    GetPlan(callee);
                }
            }
        }
    }

    /// <summary>
    /// 解析指针实参在被调形参位上的实际存储元素：实参为异类指针强转
    /// <c>(T2*)inner</c>（inner 静态类型为 T1*，T1 白名单且与 T2 不同）时
    /// 返回 T1；其余形态（裸标识符、同型强转、算术等）返回声明元素 T2。
    /// </summary>
    private static ITypeSymbol? ResolveArgumentStorageElement(
        ExpressionSyntax argument,
        SemanticModel model,
        ITypeSymbol declaredElement)
    {
        var expression = UnwrapParentheses(argument);
        if (expression is CastExpressionSyntax cast
            && cast.Type is PointerTypeSyntax
            && model.GetTypeInfo(UnwrapParentheses(cast.Expression)).Type
                is IPointerTypeSymbol innerPointer
            && IsWhitelistElement(innerPointer.PointedAtType)
            && !SymbolEqualityComparer.Default.Equals(
                innerPointer.PointedAtType, declaredElement))
        {
            return innerPointer.PointedAtType;
        }

        return declaredElement;
    }

    /// <summary>
    /// 登记重解释存储约束；同一被调方法同一形参位出现不同存储即置冲突。
    /// 返回 true 表示约束集合发生变化（调用方需失效缓存重求计划）。
    /// </summary>
    private bool RecordStorageConstraint(
        IMethodSymbol callee,
        int parameterIndex,
        ITypeSymbol storage)
    {
        if (!_reinterpretStorage.TryGetValue(callee, out var constraints))
        {
            constraints = new Dictionary<int, ITypeSymbol>();
            _reinterpretStorage[callee] = constraints;
        }

        if (constraints.TryGetValue(parameterIndex, out var existing))
        {
            if (SymbolEqualityComparer.Default.Equals(existing, storage))
                return false;
            _reinterpretConflicts.Add(callee);
            return true;
        }

        constraints[parameterIndex] = storage;
        return true;
    }

    /// <summary>
    /// 收集方法体内可证游标根符号：fixed 固定变量；初值根植于这些根的指针
    /// 局部别名（不动点）；<c>GCHandle.Alloc(arr, Pinned)</c> 句柄经
    /// <c>AddrOfPinnedObject()</c> 取出的固定数组指针局部——以上始终参与。
    /// 指针形参仅在 <paramref name="includeParameterRoots"/>（本方法自身配对
    /// 或被强制）时参与：array-only 纯下标方法的裸形参不构成外发强制边。
    /// 不进入嵌套作用域。
    /// </summary>
    private HashSet<ISymbol> CollectProvableCursorRoots(
        SyntaxNode declaration,
        SemanticModel model,
        bool includeParameterRoots)
    {
        var roots = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        // 仅本方法自身形参表；嵌套局部函数在其自身 GetPlan 中独立分析。
        if (includeParameterRoots)
        {
            if (declaration is BaseMethodDeclarationSyntax baseMethod)
            {
                foreach (var parameter in baseMethod.ParameterList.Parameters)
                {
                    if (model.GetDeclaredSymbol(parameter) is IParameterSymbol
                        { Type: IPointerTypeSymbol } pointerParameter)
                    {
                        roots.Add(pointerParameter);
                    }
                }
            }
            else if (declaration is LocalFunctionStatementSyntax localFunction)
            {
                foreach (var parameter in localFunction.ParameterList.Parameters)
                {
                    if (model.GetDeclaredSymbol(parameter) is IParameterSymbol
                        { Type: IPointerTypeSymbol } pointerParameter)
                    {
                        roots.Add(pointerParameter);
                    }
                }
            }
        }

        var nestedScopes = NestedScopes(declaration);
        bool IsInsideNestedScope(SyntaxNode node) =>
            nestedScopes.Any(n => n != node && n.Contains(node));

        foreach (var fixedDeclarator in declaration.DescendantNodes()
                     .OfType<FixedStatementSyntax>()
                     .SelectMany(f => f.Declaration.Variables))
        {
            if (model.GetDeclaredSymbol(fixedDeclarator) is { } fixedSymbol)
                roots.Add(fixedSymbol);
        }

        // 指针局部别名闭包 + Pinned 句柄指针：迭代至不动点。
        var locals = declaration.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(d => d.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() != null
                && !IsInsideNestedScope(d))
            .ToList();

        // UF040 降级域种子：裸指针 stackalloc 局部（其初值即新堆数组）始终
        // 作为可证根参与外发边强制——无论当前方法自身是否配对，被调方都必须
        // 按配对约定接收「数组 + 索引」（stackalloc 降级后调用点只有 int 索引）。
        foreach (var declarator in locals)
        {
            if (model.GetDeclaredSymbol(declarator) is { } declared
                && _stackAllocCursorRoots.Contains(declared))
            {
                roots.Add(declared);
            }
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var declarator in locals)
            {
                if (model.GetDeclaredSymbol(declarator) is not ILocalSymbol
                    { Type: IPointerTypeSymbol } local)
                {
                    continue;
                }

                if (roots.Contains(local) || declarator.Initializer?.Value is not { } initializer)
                    continue;

                if (CollectRootSymbols(initializer, model).Any(roots.Contains)
                    || IsPinnedHandlePointer(initializer, model))
                {
                    roots.Add(local);
                    changed = true;
                }
            }
        }

        return roots;
    }

    /// <summary>
    /// 是否 <c>(T*)handle.AddrOfPinnedObject()</c> 形态（handle 为任意
    /// GCHandle 局部；固定数组实参由 R3 按 Alloc 站点保守核验）。
    /// </summary>
    private static bool IsPinnedHandlePointer(
        ExpressionSyntax raw,
        SemanticModel model)
    {
        var expression = raw;
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        if (expression is not CastExpressionSyntax cast
            || cast.Type is not PointerTypeSyntax)
            return false;

        if (cast.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.ValueText != "AddrOfPinnedObject")
        {
            return false;
        }

        return model.GetSymbolInfo(memberAccess).Symbol is IMethodSymbol method
               && method.ContainingType.ToDisplayString()
                   == "System.Runtime.InteropServices.GCHandle";
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

    /// <summary>
    /// 信号⑧：把<b>携带非零偏移</b>的本方法指针形参游标（<c>p ± n</c> 等
    /// 非裸指针表达式，裸别名局部经闭包追溯到形参）作为指针实参外发给含白名单
    /// 指针形参的方法（含元数据/extern 方法，如
    /// <c>Encoding.UTF8.GetBytes(pInput + next, ...)</c>）。array-only 降级
    /// 得到 <c>T[] p</c> 后无法表达 <c>p + n</c> 这个实参偏移，只有配对约定的
    /// <c>pIndex + n</c> 可承载。裸形参实参（<c>F(p)</c>）不触发——整组数组
    /// 可直接传递；取址 <c>&amp;p[i]</c>/指针强转实参各有既有通道，不在此列。
    /// 仅根植于 fixed/局部游标（不含形参根）的偏移实参由 R3 降级表承载。
    /// </summary>
    private static bool BodyForwardsParamOffsetArgument(
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

        var closure = BuildLocalAliasClosure(declaration, model, IsInsideNestedScope);

        foreach (var invocation in declaration.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (IsInsideNestedScope(invocation)) continue;

            var callees = GetInvocationMethods(model, invocation).ToList();
            if (!callees.Any(c => c.Parameters.Any(p =>
                    p.Type is IPointerTypeSymbol pointer
                    && IsWhitelistElement(pointer.PointedAtType))))
            {
                continue;
            }

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (model.GetTypeInfo(argument.Expression).Type is not IPointerTypeSymbol)
                    continue;

                var expression = UnwrapParentheses(argument.Expression);

                // 重解释实参 (T2*)inner：剥掉一层白名单异类/同型指针强转，按
                // inner 判定（inner 为形参 ± 常量时本方法仍须配对，被调方按
                // 重解释存储约定接收——见 TryApplyReinterpretStorage）。
                var probe = expression;
                if (expression is CastExpressionSyntax pointerCast
                    && pointerCast.Type is PointerTypeSyntax
                    && model.GetTypeInfo(UnwrapParentheses(pointerCast.Expression)).Type
                        is IPointerTypeSymbol innerPointerType
                    && IsWhitelistElement(innerPointerType.PointedAtType))
                {
                    probe = UnwrapParentheses(pointerCast.Expression);
                }

                // 裸形参/裸别名（含裸强转 (T2*)p）：整组数组可直接传递，
                // array-only 足以承载。
                if (probe is IdentifierNameSyntax)
                    continue;
                // 取址实参走 &arr[i] 通道；其余指针强转实参走 UF021/R6 既有判决。
                if (probe is PrefixUnaryExpressionSyntax
                    { RawKind: (int)SyntaxKind.AddressOfExpression })
                {
                    continue;
                }

                if (probe is CastExpressionSyntax)
                    continue;

                var roots = ExpandRoots(
                    CollectRootSymbols(probe, model), closure);
                if (roots.Any(pointerParams.Contains))
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

    /// <summary>
    /// 表达式是否为清单资源流指针 <c>stream.PositionPointer</c>：stream 必须是
    /// 局部，其声明初值（可去一层强转）为
    /// <c>System.Reflection.Assembly.GetManifestResourceStream(...)</c>。
    /// 命中时输出 stream 局部名。R5（字段写 RHS 资格）与 R3（资源指针局部
    /// 降级）共享此判定，保证字段来源证明与局部降级口径一致。
    /// </summary>
    public static bool IsManifestResourcePositionPointer(
        ExpressionSyntax raw,
        SemanticModel model,
        out string streamLocalName,
        OriginalSyntaxResolver? resolver = null)
    {
        streamLocalName = string.Empty;

        ExpressionSyntax? ForModel(ExpressionSyntax node)
        {
            if (node.SyntaxTree == model.SyntaxTree) return node;
            return resolver?.Resolve(node) as ExpressionSyntax;
        }

        var expression = UnwrapParentheses(raw);
        if (expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.ValueText != "PositionPointer")
        {
            return false;
        }

        if (ForModel(memberAccess) is not { } modelMemberAccess
            || model.GetSymbolInfo(modelMemberAccess).Symbol is not IPropertySymbol property
            || property.ContainingType.ToDisplayString() != "System.IO.UnmanagedMemoryStream")
        {
            return false;
        }

        if (ForModel(memberAccess.Expression) is not { } modelStreamReceiver
            || model.GetSymbolInfo(modelStreamReceiver).Symbol is not ILocalSymbol streamLocal)
            return false;

        streamLocalName = streamLocal.Name;

        var streamDeclarator = streamLocal.DeclaringSyntaxReferences
            .FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
        if (streamDeclarator?.Initializer?.Value is not { } streamRaw)
            return false;

        var streamInit = UnwrapParentheses(streamRaw);
        if (streamInit is CastExpressionSyntax streamCast)
            streamInit = UnwrapParentheses(streamCast.Expression);
        if (streamInit is not InvocationExpressionSyntax streamInvocation)
            return false;

        if (model.GetSymbolInfo(streamInvocation).Symbol is not IMethodSymbol streamMethod
            || streamMethod.Name != "GetManifestResourceStream")
        {
            return false;
        }

        var containingType = streamMethod.ContainingType;
        while (containingType != null)
        {
            if (containingType.ToDisplayString() == "System.Reflection.Assembly")
                return true;
            containingType = containingType.BaseType;
        }

        return false;
    }

    internal static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    /// <summary>
    /// 匹配标量槽位重解释 <c>*((T*)&amp;scalar)</c> 的同尺寸基元配对：
    /// double→long、int/uint→float、long/ulong→double。取址操作数必须是
    /// 局部/形参简单标量。命中输出对应的 <c>System.BitConverter</c> 方法名；
    /// 无符号来源需要 unchecked 同尺寸有符号强转时输出目标关键字。
    /// R3 据此放行（不前置 UF020），R6 据此生成归一调用。
    /// </summary>
    public static bool TryMatchScalarReinterpret(
        ExpressionSyntax rawOperand,
        SemanticModel model,
        OriginalSyntaxResolver? resolver,
        out string bitConverterMethod,
        out SyntaxKind? signedCastKeyword)
    {
        bitConverterMethod = string.Empty;
        signedCastKeyword = null;

        ExpressionSyntax? ForModel(ExpressionSyntax node)
            => node.SyntaxTree == model.SyntaxTree
                ? node
                : resolver?.Resolve(node) as ExpressionSyntax;

        var operand = UnwrapParentheses(rawOperand);
        if (operand is not CastExpressionSyntax cast
            || UnwrapParentheses(cast.Expression) is not PrefixUnaryExpressionSyntax
                   { RawKind: (int)SyntaxKind.AddressOfExpression } address)
        {
            return false;
        }

        var scalar = UnwrapParentheses(address.Operand);
        if (scalar is not IdentifierNameSyntax
            || ForModel(scalar) is not { } modelScalar
            || model.GetSymbolInfo(modelScalar).Symbol is not (ILocalSymbol or IParameterSymbol))
        {
            return false;
        }

        if (ForModel(cast) is not { } modelCast
            || model.GetTypeInfo(modelCast).Type is not IPointerTypeSymbol castPointer)
        {
            return false;
        }

        var sourceType = model.GetTypeInfo(modelScalar).Type;
        if (sourceType == null)
            return false;

        (string? Method, SyntaxKind? Keyword)? match =
            (sourceType.SpecialType, castPointer.PointedAtType.SpecialType) switch
            {
                (SpecialType.System_Double, SpecialType.System_Int64) => ("DoubleToInt64Bits", (SyntaxKind?)null),
                (SpecialType.System_Int32, SpecialType.System_Single) => ("Int32BitsToSingle", null),
                (SpecialType.System_UInt32, SpecialType.System_Single) => ("Int32BitsToSingle", SyntaxKind.IntKeyword),
                (SpecialType.System_Int64, SpecialType.System_Double) => ("Int64BitsToDouble", null),
                (SpecialType.System_UInt64, SpecialType.System_Double) => ("Int64BitsToDouble", SyntaxKind.LongKeyword),
                _ => (null, null),
            };

        if (match is not { } matched || matched.Method == null)
            return false;

        bitConverterMethod = matched.Method;
        signedCastKeyword = matched.Keyword;
        return true;
    }

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

    /// <summary>
    /// 末端指针形参（pEnd/pCharsEndPos 约定）同源分组的基指针形参；null 表示
    /// 该形参自身即源槽代表。决定 R5 flow 登记的源槽归属。
    /// </summary>
    public IParameterSymbol? SlotBaseSymbol { get; init; }

    /// <summary>
    /// 重解释存储元素：形参声明为 T2* 但唯一调用族以 <c>(T2*)(T1* p)</c>
    /// 传入异类存储（如 long* 形参实由 char[] 承载）时为 T1。R5 数组形参用
    /// T1[]，R6 体内 <c>*(T3*)(p±k)</c> 按字节偏移在 T1 存储上打包读取；
    /// null 表示常规同元素配对。
    /// </summary>
    public ITypeSymbol? StorageElement { get; init; }
}

internal sealed record PairedMethodPlan
{
    public required IMethodSymbol Method { get; init; }
    public required bool ReturnsPointer { get; init; }
    public required ITypeSymbol? ReturnElement { get; init; }

    /// <summary>与原方法形参逐位对齐的计划（一个原参数一个条目）。</summary>
    public required List<PairedParamPlan> Parameters { get; init; }
}
