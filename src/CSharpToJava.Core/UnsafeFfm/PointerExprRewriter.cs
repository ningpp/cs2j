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
    private readonly IReadOnlyDictionary<ISymbol, (string ArrayName, bool ReadOnly, string SourceSlot)>
        _loweredArrayMap;
    private readonly PointerMethodCatalog _catalog;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;
    private IndexPlan? _plan;

    /// <summary>
    /// UF040 降级域：prescan 判定可降级的裸指针 stackalloc 局部（原始符号）。
    /// 仅当调用的实参根命中本集合时，R6 才启用「元数据指针方法→同名数组+
    /// 索引托管重载」展开，把新行为严格限定在本次 stackalloc 降级触及的
    /// 调用内（既有 fixed/参数路径行为不变）。
    /// </summary>
    private readonly IReadOnlyCollection<ISymbol> _stackAllocCursorSymbols;

    /// <summary>
    /// 其中索引全程恒为 0 的 stackalloc 种子（无自增/偏移重绑定）：元数据
    /// 数组重载展开时允许消去与缓冲等长的容量（byteCount 形态）实参。
    /// </summary>
    private readonly IReadOnlyCollection<ISymbol> _stackAllocZeroInvariantSymbols;

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
        IReadOnlyDictionary<ISymbol, (string ArrayName, bool ReadOnly, string SourceSlot)>
            loweredArrayMap,
        PointerMethodCatalog catalog,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver,
        CompilationUnitSyntax root,
        IReadOnlyCollection<ISymbol>? stackAllocCursorSymbols = null,
        IReadOnlyCollection<ISymbol>? stackAllocZeroInvariantSymbols = null)
    {
        _model = model;
        _flowModel = flowModel;
        _loweredSymbols = loweredSymbols;
        _loweredArrayMap = loweredArrayMap;
        _catalog = catalog;
        _statistics = statistics;
        _resolver = resolver;
        _stackAllocCursorSymbols = stackAllocCursorSymbols
            ?? Array.Empty<ISymbol>();
        _stackAllocZeroInvariantSymbols = stackAllocZeroInvariantSymbols
            ?? Array.Empty<ISymbol>();
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
            return ExpandPairedInvocation(node, plan, out _, out _);

        if (IsHolderCreateInvocation(node))
            return RebuildHolderCreateInvocation(node, out _);

        // UF040 降级域：含 stackalloc 降级游标实参的元数据指针方法
        // （Encoding.UTF8.GetBytes 指针重载等）→ 同名托管数组+索引重载。
        if (TryRewriteMetadataPointerInvocation(node, out var metadataRewritten, out var metadataError))
        {
            if (metadataRewritten != null) return metadataRewritten;
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                metadataError!,
                node);
            return node;
        }

        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitObjectCreationExpression(
        ObjectCreationExpressionSyntax node)
    {
        if (TryRewriteStringPointerConstruction(node, out var rewritten))
            return rewritten;

        return base.VisitObjectCreationExpression(node);
    }

    /// <summary>
    /// <c>new string(char* chars, int startIndex, int length)</c> 指针构造
    /// （配对/fixed 可索引域）→ 托管 <c>new string(char[], int, int)</c>：
    /// 起始下标为指针索引与原 startIndex 之和（原语义为 chars + startIndex）。
    /// 指针实参不可证为数组+索引时不接管（交既有门控判决）。
    /// </summary>
    private bool TryRewriteStringPointerConstruction(
        ObjectCreationExpressionSyntax node,
        out ExpressionSyntax? rewritten)
    {
        rewritten = null;
        if (node.ArgumentList is not { } argumentList
            || argumentList.Arguments.Count != 3)
        {
            return false;
        }

        var original = _resolver.Resolve(node) as ObjectCreationExpressionSyntax
            ?? node;
        if (_model.GetSymbolInfo(original).Symbol is not IMethodSymbol constructor)
            return false;

        var parameters = constructor.Parameters;
        var charType = _model.Compilation.GetSpecialType(SpecialType.System_Char);
        if (constructor.ContainingType.SpecialType != SpecialType.System_String
            || parameters.Length != 3
            || parameters[0].Type is not IPointerTypeSymbol charPointer
            || !SymbolEqualityComparer.Default.Equals(
                charPointer.PointedAtType, charType))
        {
            return false;
        }

        var pointerArgument = argumentList.Arguments[0].Expression;
        if (!TryBuildIndex(pointerArgument, out var arrayText, out var index, out _))
            return false;

        var startIndex = (ExpressionSyntax)Visit(
            argumentList.Arguments[1].Expression)!;
        var length = (ExpressionSyntax)Visit(
            argumentList.Arguments[2].Expression)!;

        var mergedStart = Simplify(BinaryExpression(
            SyntaxKind.AddExpression,
            ParenthesizedExpression(index),
            ParenthesizedExpression(startIndex)));

        _statistics.PointerExprsIndexized++;
        rewritten = node.WithArgumentList(ArgumentList(SeparatedList(new[]
        {
            Argument(ParseExpression(arrayText)),
            Argument(mergedStart),
            Argument(length),
        })));
        return true;
    }

    /// <summary>
    /// 按配对计划逐位重建实参表：PlainPointer 槽把指针实参展开为
    /// 「数组实参 + int 索引实参」；HolderPointer 槽在 holder 实参后插入其
    /// 伴生数组实参；None 槽原样访问。receiver/静态类型限定整体保留。
    /// <paramref name="firstArray"/> 为第一个成功解析的指针槽数组名
    /// （指针返回结果的同源数组载体——配对约定下返回索引相对调用方传入的
    /// 同入参数组空间；调用点一律将结果赋回/返回输出游标，元素访问经该
    /// 游标自身的 flow/降级表解析，不依赖此处数组名）；
    /// <paramref name="firstSlot"/> 为该实参根的真源源槽键。
    /// </summary>
    private InvocationExpressionSyntax ExpandPairedInvocation(
        InvocationExpressionSyntax invocation,
        PairedMethodPlan plan,
        out string? firstArray,
        out string? firstSlot)
    {
        firstArray = null;
        firstSlot = null;
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
                // 重解释存储槽：实参形如 (T2*)(p ± k)，数组/索引须按强转内的
                // 异类 T1* 表达式构造（数组类型为存储元素 T1[]）。
                var indexArg = rawArg;
                if (slot.StorageElement != null
                    && rawArg is CastExpressionSyntax storageCast
                    && storageCast.Type is PointerTypeSyntax)
                {
                    indexArg = UnwrapParenthesized(storageCast.Expression);
                }

                if (TryBuildIndex(indexArg, out var argArray, out var argIndex,
                        out var argSlot))
                {
                    firstArray ??= argArray;
                    firstSlot ??= argSlot;
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

    // ---- UF040 降级域：元数据指针方法 → 同名托管数组+索引重载 ----

    /// <summary>
    /// stackalloc 降级游标作为实参外发到元数据/外部指针方法（典型：
    /// <c>Encoding.UTF8.GetBytes(char*, int, byte*, int)</c> 不安全重载）时，
    /// 把每个白名单指针形参位的实参展开为「数组实参 + int 索引实参」，其余
    /// 实参照常访问，并在被调声明类型的同名方法中按展开后形参类型序列匹配
    /// 全托管数组重载（6 参 GetBytes 形态）重建调用。
    /// <para>门控：至少一个指针实参的根符号溯源到 prescan 登记的 stackalloc
    /// 种子（含其同槽别名，按 R3 源槽判定）；既有 fixed/形参 int 索引通道
    /// （信号⑧）不经本方法，行为不变。</para>
    /// <para>后备：展开全匹配失败时，若缓冲指针实参根为零不变种子，允许消去
    /// 其后紧邻的 int 容量实参（5 参 GetBytes 形态）再匹配。全部失败不静默
    /// 吞错——返回 handled=true + error，调用方发 UF020。</para>
    /// </summary>
    private bool TryRewriteMetadataPointerInvocation(
        InvocationExpressionSyntax node,
        out InvocationExpressionSyntax? rewritten,
        out string? error)
    {
        rewritten = null;
        error = null;

        if (_resolver.Resolve(node) is not InvocationExpressionSyntax originalInvocation)
            return false;

        var symbolInfo = _model.GetSymbolInfo(originalInvocation);
        var candidates = new List<IMethodSymbol>();
        if (symbolInfo.Symbol is IMethodSymbol direct) candidates.Add(direct);
        foreach (var candidate in symbolInfo.CandidateSymbols)
        {
            if (candidate is IMethodSymbol candidateMethod)
                candidates.Add(candidateMethod);
        }

        // 源码方法走配对计划通道；本方法只接管无源码声明的元数据/外部方法。
        candidates.RemoveAll(method => method.DeclaringSyntaxReferences.Length != 0);
        // 只接管含指针形参的重载；普通托管方法不走本展开。
        candidates.RemoveAll(method => !method.Parameters.Any(
            p => p.Type is IPointerTypeSymbol));
        if (candidates.Count == 0) return false;

        // 门控：至少一个指针实参的根符号溯源到 prescan 登记的 stackalloc
        // 降级种子（含其同槽别名，按 R3 源槽判定）。fixed/形参可证游标外发
        // 元数据方法的形态仍走既有信号⑧ VisitArgument int 索引通道，行为不变。
        var hasStackAllocCursorArg = false;
        foreach (var argument in node.ArgumentList.Arguments)
        {
            if (!IsOldPointerTyped(argument.Expression)) continue;

            var argumentRoots = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            CollectOriginatingSymbols(argument.Expression, argumentRoots);
            if (argumentRoots.Any(IsStackAllocSlotRoot))
            {
                hasStackAllocCursorArg = true;
                break;
            }
        }

        // 降级 fallback：上门控未命中但至少一个 int 型实参（已被先前 visit
        // 降至 int）的 originating symbols 溯源到被 R5 配对形参的 offset 变量
        // （如 pInputIndex）。 此时仍尝试托管重载展开——以「数组 + offset 变量」
        // 替换指针位。 仅当确实存在此类 offset 实参时才接管，避免对其他方法产生
        // 副作用。 展开失败同样吞错并保真原文（与 stackalloc 域一致）。
        if (!hasStackAllocCursorArg)
        {
            if (!HasPairedParameterOffsetArg(node.ArgumentList.Arguments))
                return false;
            return TryExpandMetadataInvocationAsPairedOffset(node, out rewritten, out error);
        }

        string? firstError = null;
        foreach (var method in candidates)
        {
            if (!TryBuildMetadataExpansion(
                    node, method, out var records, out var expandError))
            {
                firstError ??= expandError;
                continue;
            }

            var pointerArgCount = records.Count(r => r.Kind == MetadataArgKind.Pointer);

            if (TryConcatRecords(records, out var fullSlots)
                && TryFindManagedArrayOverload(method, fullSlots, out _))
            {
                rewritten = RebuildMetadataInvocation(node, records, pointerArgCount);
                return true;
            }

            // 零不变缓冲后备：消去指针缓冲位后紧邻的 int 容量实参。
            if (TryDropCapacityRecord(records)
                && TryConcatRecords(records, out var reducedSlots)
                && TryFindManagedArrayOverload(method, reducedSlots, out _))
            {
                rewritten = RebuildMetadataInvocation(node, records, pointerArgCount);
                return true;
            }

            firstError ??= $"metadata pointer method '{method.Name}' receives a stackalloc cursor " +
                "but no same-named managed overload taking arrays and indexes matches the expanded " +
                "arguments; the original file was preserved.";
        }

        error = firstError ?? "metadata pointer call with a stackalloc cursor cannot be resolved "
            + "to a managed array overload; the original file was preserved.";
        return true;
    }

    /// <summary>
    /// 网关助手：arg 列表中存在 int 型实参（已被先前 visit 降至 int），且其
    /// originating symbols 溯源到一个 R5 配对形参的 offset 变量（如 pInputIndex）。
    /// </summary>
    private bool HasPairedParameterOffsetArg(IReadOnlyList<ArgumentSyntax> arguments)
    {
        foreach (var argument in arguments)
        {
            var roots = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            CollectOriginatingSymbols(argument.Expression, roots);
            if (roots.Any(IsPairedParameterOffsetRoot))
                return true;
            // R5 lowered fallback：CollectOriginatingSymbols 依赖
            // GetExpressionSymbol 映射回原树，对 R5 新引入的配对索引标识符
            // （如 pInputIndex）返回 null；需要直接拿表达式中的标识符文本与
            // flow 模型 OffsetText 比对。
            if (ExpressionContainsPairedOffsetName(argument.Expression))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 递归检查表达式树中是否存在某个标识符的文本名称匹配 flow 模型中某配对
    /// 指针形参 flow 的 OffsetText（如 pInputIndex）。用于 R5 降级 fallback：
    /// R5 引入的配对索引变量是新节点，解析器无法映射回原始树，
    /// <see cref="CollectOriginatingSymbols"/> 会遗漏它；此处按表达式结构
    /// 直接搜索。
    /// </summary>
    private bool ExpressionContainsPairedOffsetName(ExpressionSyntax expression)
    {
        expression = UnwrapParenthesized(expression);
        if (expression is IdentifierNameSyntax identifier)
        {
            return _flowModel.AnyPairedParameterWithOffset(identifier.Identifier.ValueText);
        }
        if (expression is BinaryExpressionSyntax binary)
        {
            return ExpressionContainsPairedOffsetName(binary.Left)
                || ExpressionContainsPairedOffsetName(binary.Right);
        }
        if (expression is PrefixUnaryExpressionSyntax prefix)
        {
            return ExpressionContainsPairedOffsetName(prefix.Operand);
        }
        if (expression is CastExpressionSyntax cast)
        {
            return ExpressionContainsPairedOffsetName(cast.Expression);
        }
        return false;
    }

    /// <summary>
    /// 降级 fallback：上门控（stackalloc）未命中但 HasPairedParameterOffsetArg 命中时，
    /// 复用栈上展开路径（candidates/TryBuildMetadataExpansion/RebuildMetadataInvocation），
    /// 只是把 bool 门控置为 true 去重走 TryBuildMetadataExpansion。 展开失败后
    /// 吞错保真原文，与 stackalloc 域行为一致。
    /// </summary>
    private bool TryExpandMetadataInvocationAsPairedOffset(
        InvocationExpressionSyntax node,
        out InvocationExpressionSyntax? rewritten,
        out string? error)
    {
        rewritten = null;
        error = null;

        // 与 TryRewriteMetadataPointerInvocation 共享 candidates 计算。
        if (_resolver.Resolve(node) is not InvocationExpressionSyntax originalInvocation)
            return false;

        var symbolInfo = _model.GetSymbolInfo(originalInvocation);
        var candidates = new List<IMethodSymbol>();
        if (symbolInfo.Symbol is IMethodSymbol direct) candidates.Add(direct);
        foreach (var candidate in symbolInfo.CandidateSymbols)
        {
            if (candidate is IMethodSymbol candidateMethod)
                candidates.Add(candidateMethod);
        }
        candidates.RemoveAll(method => method.DeclaringSyntaxReferences.Length != 0);
        candidates.RemoveAll(method => !method.Parameters.Any(p => p.Type is IPointerTypeSymbol));
        if (candidates.Count == 0) return false;

        string? firstError = null;
        foreach (var method in candidates)
        {
            if (!TryBuildMetadataExpansion(node, method, out var records, out var expandError))
            {
                firstError ??= expandError;
                continue;
            }

            var pointerArgCount = records.Count(r => r.Kind == MetadataArgKind.Pointer);
            if (TryConcatRecords(records, out var fullSlots)
                && TryFindManagedArrayOverload(method, fullSlots, out _))
            {
                rewritten = RebuildMetadataInvocation(node, records, pointerArgCount);
                return true;
            }
            if (TryDropCapacityRecord(records)
                && TryConcatRecords(records, out var reducedSlots)
                && TryFindManagedArrayOverload(method, reducedSlots, out _))
            {
                rewritten = RebuildMetadataInvocation(node, records, pointerArgCount);
                return true;
            }
            firstError ??= $"metadata pointer method '{method.Name}' receives a paired-parameter "
                + "offset argument but no managed array overload matches; the original file was preserved.";
        }

        error = firstError ?? "metadata pointer call with a paired-parameter offset argument "
            + "cannot be resolved to a managed array overload; the original file was preserved.";
        return true;
    }

    /// <summary>
    /// 降级 fallback：originating symbols 中的某个符号溯源到 R5 配对指针形参。
    /// 两种路径：
    /// <list type="bullet">
    /// <item>符号名称为某 R5 配对指针形参 flow 的 OffsetText（如 pInputIndex）。
    /// 该 int 不作为 key 注册（key 是 pInput 指针符号），按偏移名称反查；</item>
    /// <item>符号自身即 flow 的注册 key 且 FoldsToOffset（key 是 pInput 指针符号，
    /// OffsetText 空/不匹配配对形参的 IndexName≠形参名时原始 pInput 自身也配对）。
    /// VisitInvocationExpression 的栈上 fallback 门控在原始树形态下拿到的是
    /// pInput 指针原始符号（而非已降级的 pInputIndex int），不能仅靠名称
    /// 命中 OffsetText。</item>
    /// </list>
    /// </summary>
    private bool IsPairedParameterOffsetRoot(ISymbol? root)
    {
        if (root == null) return false;
        // 路径A：名称为某配对 flow 的 OffsetText（pInputIndex）。
        if (_flowModel.AnyPairedParameterWithOffset(root.Name)) return true;
        // 路径B：符号自身即注册的配对指针形参 key（pInput）。
        // 配对约定在 R5 以 FoldsToOffset=true 登记；array-only 未配对则
        // FoldsToOffset=false，两路径必须同时命中才不误触发 fallback。
        if (_flowModel.TryGet(root, out var selfFlow) && selfFlow.FoldsToOffset) return true;
        return false;
    }

    /// <summary>
    /// 降级 fallback：某标识符是否为一个 R5 配对指针形参的「int 索引偏移变量」
    /// （即 _flowModel 中以 OffsetText 登记的 pInputIndex 等）。该符号自身未中
    /// 心注册（注册 key 是 pInput 指针符号），但 offset 名称命中即视为配对索引。
    /// </summary>
    private bool IsPairedIndexIdentifier(ExpressionSyntax expression)
    {
        if (UnwrapParenthesized(expression) is not IdentifierNameSyntax identifier)
            return false;
        var symbol = GetExpressionSymbol(identifier);
        if (symbol != null)
            return _flowModel.AnyPairedParameterWithOffset(symbol.Name);
        // R5 lowered fallback: pInputIndex 是 R5 引入的新标识符，解析器无法
        // 映射回原树，GetExpressionSymbol 返回 null；直接拿标识符文本与 flow
        // 模型 OffsetText 比对。
        return _flowModel.AnyPairedParameterWithOffset(identifier.Identifier.ValueText);
    }

    private enum MetadataArgKind { Plain, Pointer }

    private sealed class MetadataArgRecord
    {
        public required MetadataArgKind Kind { get; init; }
        public required List<ITypeSymbol> SlotTypes { get; init; }

        /// <summary>Pointer 位展开后的两个实参（数组 + 索引）。</summary>
        public List<ArgumentSyntax>? ExpandedArguments { get; init; }

        /// <summary>Plain 位原始实参（重建时才访问，避免候选探测产生副作用）。</summary>
        public ArgumentSyntax? RawArgument { get; init; }

        /// <summary>指针位：其根是否全部为零不变 stackalloc 种子（容量后备门）。</summary>
        public bool RootZeroInvariant { get; init; }
    }

    private bool TryBuildMetadataExpansion(
        InvocationExpressionSyntax node,
        IMethodSymbol method,
        out List<MetadataArgRecord> records,
        out string? error)
    {
        records = new List<MetadataArgRecord>();
        error = null;

        foreach (var argument in node.ArgumentList.Arguments)
        {
            if (!TryGetParameterForArgument(method, argument, node.ArgumentList, out var parameter)
                || parameter is null)
            {
                error = $"argument '{argument}' of metadata method '{method.Name}' cannot be mapped " +
                    "to a by-value parameter (named/params/ref shapes at a stackalloc cursor call " +
                    "are not supported); the original file was preserved.";
                return false;
            }

            if (parameter.Type is IPointerTypeSymbol pointerParameter)
            {
                if (!PointerMethodCatalog.IsWhitelistElement(pointerParameter.PointedAtType))
                {
                    error = $"metadata method '{method.Name}' pointer parameter '{parameter.Name}' " +
                        "has a non-whitelist element type (void*/generic/struct pointer); the " +
                        "original file was preserved.";
                    return false;
                }

                if (!TryBuildIndex(argument.Expression,
                        out var arrayText, out var index, out _))
                {
                    error = $"pointer argument '{argument.Expression}' passed to metadata method " +
                        $"'{method.Name}' has no provable array/index origin; the original file " +
                        "was preserved.";
                    return false;
                }

                var roots = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                CollectOriginatingSymbols(argument.Expression, roots);
                var stackAllocRoots = roots.Where(IsStackAllocSlotRoot).ToList();

                // 容量后备（5 参形态）要求展开的缓冲索引恒为 0：
                // ① 零不变 stackalloc 种子；② 展开索引即常量 0；
                // ③ 裸引用的 fixed/Pinned 游标初值零偏移且作用域内无写入。
                var zeroInvariant =
                    (stackAllocRoots.Count > 0
                        && stackAllocRoots.All(IsZeroInvariantStackAllocRoot))
                    || IsConstantZeroLiteral(index)
                    || (IsBareIdentifierExpression(argument.Expression)
                        && roots.Count > 0
                        && roots.All(IsFixedCursorZeroInvariant));

                records.Add(new MetadataArgRecord
                {
                    Kind = MetadataArgKind.Pointer,
                    ExpandedArguments = new List<ArgumentSyntax>
                    {
                        Argument(ParseExpression(arrayText)),
                        Argument(Simplify(index)),
                    },
                    SlotTypes = new List<ITypeSymbol>
                    {
                        _model.Compilation.CreateArrayTypeSymbol(pointerParameter.PointedAtType),
                        _model.Compilation.GetSpecialType(SpecialType.System_Int32),
                    },
                    // zeroInvariant 涵盖：零不变 stackalloc 种子、展开索引为常量 0、
                    // 裸引用 fixed/Pinned 游标初值零偏移且作用域内无写入——任何
                    // 一种形态成立，尾部 int 容量位均可安全消去。
                    RootZeroInvariant = zeroInvariant,
                });
                continue;
            }

            records.Add(new MetadataArgRecord
            {
                Kind = MetadataArgKind.Plain,
                RawArgument = argument,
                SlotTypes = new List<ITypeSymbol> { parameter.Type },
            });
        }

        return true;
    }

    private static bool TryConcatRecords(
        List<MetadataArgRecord> records,
        out List<ITypeSymbol> slotTypes)
    {
        slotTypes = new List<ITypeSymbol>();
        foreach (var record in records)
            slotTypes.AddRange(record.SlotTypes);

        return slotTypes.Count > 0;
    }

    /// <summary>
    /// 消去最后一个「零不变指针位」之后紧邻的 int 普通实参（指针重载中的
    /// 缓冲容量位，如 GetBytes 尾部 byteCount）；要求指针位与容量位在原调用
    /// 中相邻。仅在全展开无匹配时尝试一次。
    /// </summary>
    private static bool TryDropCapacityRecord(List<MetadataArgRecord> records)
    {
        for (var i = records.Count - 2; i >= 0; i--)
        {
            if (records[i].Kind != MetadataArgKind.Pointer || !records[i].RootZeroInvariant)
                continue;

            var following = records[i + 1];
            if (following.Kind == MetadataArgKind.Plain
                && following.SlotTypes.Count == 1
                && following.SlotTypes[0].SpecialType == SpecialType.System_Int32)
            {
                records.RemoveAt(i + 1);
                return true;
            }

            return false;
        }

        return false;
    }

    private InvocationExpressionSyntax RebuildMetadataInvocation(
        InvocationExpressionSyntax node,
        List<MetadataArgRecord> records,
        int pointerArgCount)
    {
        _statistics.PointerExprsIndexized += pointerArgCount;

        var arguments = new List<ArgumentSyntax>();
        foreach (var record in records)
        {
            if (record.Kind == MetadataArgKind.Pointer)
            {
                arguments.AddRange(record.ExpandedArguments!);
            }
            else
            {
                // 此刻才访问普通实参（经既有 VisitArgument 全部门控）。
                arguments.Add((ArgumentSyntax)Visit(record.RawArgument!)!);
            }
        }

        var visitedExpression = (ExpressionSyntax)Visit(node.Expression)!;
        return node
            .WithExpression(visitedExpression)
            .WithArgumentList(ArgumentList(SeparatedList(arguments)));
    }

    /// <summary>
    /// 在被调声明类型（含基类链）的同名方法中匹配全托管重载：形参数与展开
    /// 槽位数相等、全部按值传递且无指针形参，逐位类型恒等（byte/char/int
    /// 及其数组）。
    /// </summary>
    private static bool TryFindManagedArrayOverload(
        IMethodSymbol pointerMethod,
        IReadOnlyList<ITypeSymbol> slotTypes,
        out IMethodSymbol? match)
    {
        match = null;
        var type = pointerMethod.ContainingType;
        while (type != null)
        {
            foreach (var member in type.GetMembers(pointerMethod.Name))
            {
                if (member is not IMethodSymbol candidate
                    || candidate.MethodKind != MethodKind.Ordinary
                    || candidate.IsGenericMethod
                    || candidate.Parameters.Length != slotTypes.Count)
                    continue;

                if (candidate.Parameters.Any(p =>
                        p.RefKind != RefKind.None || p.Type is IPointerTypeSymbol))
                    continue;

                var allMatch = true;
                for (var i = 0; i < slotTypes.Count; i++)
                {
                    if (!SymbolEqualityComparer.Default.Equals(
                            candidate.Parameters[i].Type, slotTypes[i]))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                {
                    match = candidate;
                    return true;
                }
            }

            type = type.BaseType;
        }

        return false;
    }

    /// <summary>按位置/命名实参映射形参（params 形态不支持，返回 false）。</summary>
    private static bool TryGetParameterForArgument(
        IMethodSymbol method,
        ArgumentSyntax argument,
        ArgumentListSyntax argumentList,
        out IParameterSymbol? parameter)
    {
        parameter = null;

        if (argument.RefKindKeyword.Kind() != SyntaxKind.None)
            return false;

        if (argument.NameColon?.Name is { } named)
        {
            parameter = method.Parameters.FirstOrDefault(p =>
                p.Name == named.Identifier.ValueText);
            return parameter != null;
        }

        var index = argumentList.Arguments.IndexOf(argument);
        if (index < 0 || index >= method.Parameters.Length) return false;

        var candidate = method.Parameters[index];
        if (candidate.IsParams) return false;
        parameter = candidate;
        return true;
    }

    /// <summary>
    /// 表达式根符号是否溯源到 stackalloc 降级种子：种子自身命中，或 R3 降级
    /// 局部的真源源槽与某种子的伴生数组同名（种子的裸/偏移别名）。
    /// </summary>
    private bool IsStackAllocSlotRoot(ISymbol? root)
    {
        if (root == null || !IsOldPointerSymbol(root)) return false;
        if (_stackAllocCursorSymbols.Contains(root)) return true;

        return _loweredArrayMap.TryGetValue(root, out var info)
            && _stackAllocCursorSymbols.Any(seed =>
                _loweredArrayMap.TryGetValue(seed, out var seedInfo)
                && seedInfo.SourceSlot == info.SourceSlot);
    }

    /// <summary>根是否为 prescan 判定全程索引恒 0 的 stackalloc 种子。</summary>
    private bool IsZeroInvariantStackAllocRoot(ISymbol? root)
        => root != null && _stackAllocZeroInvariantSymbols.Contains(root);

    private static bool IsConstantZeroLiteral(ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax
            {
                RawKind: (int)SyntaxKind.NumericLiteralExpression
            } literal
            && literal.Token.ValueText == "0";

    private static bool IsBareIdentifierExpression(ExpressionSyntax expression)
        => UnwrapParenthesized(expression) is IdentifierNameSyntax;

    /// <summary>
    /// fixed 指针游标在其 fixed 作用域内索引是否恒为 0：初值为数组本身或
    /// <c>&amp;arr[0]</c>（零偏移），且作用域内不存在赋值/自增/ref-out 外发。
    /// Pinned 句柄重绑定等非 fixed 声明形态保守返回 false。
    /// </summary>
    private bool IsFixedCursorZeroInvariant(ISymbol? root)
    {
        if (root is not ILocalSymbol { Type: IPointerTypeSymbol } local)
            return false;
        if (!_loweredArrayMap.ContainsKey(local))
            return false;

        var reference = local.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference?.GetSyntax() is not VariableDeclaratorSyntax declarator)
            return false;
        if (declarator.FirstAncestorOrSelf<FixedStatementSyntax>()
            is not { } fixedStatement)
        {
            return false;
        }

        if (!HasZeroOffsetFixedInitializer(declarator.Initializer?.Value))
            return false;

        var scope = fixedStatement.Statement;
        foreach (var descendant in scope.DescendantNodes())
        {
            switch (descendant)
            {
                case AssignmentExpressionSyntax assignment
                    when SymbolEqualsOriginal(assignment.Left, local):
                case PrefixUnaryExpressionSyntax prefix
                    when prefix.Operand is ExpressionSyntax prefixOperand
                        && (prefix.IsKind(SyntaxKind.PreIncrementExpression)
                            || prefix.IsKind(SyntaxKind.PreDecrementExpression)
                            || prefix.IsKind(SyntaxKind.AddressOfExpression))
                        && SymbolEqualsOriginal(prefixOperand, local):
                    return false;
                case PostfixUnaryExpressionSyntax postfix
                    when postfix.Operand is ExpressionSyntax postfixOperand
                        && (postfix.IsKind(SyntaxKind.PostIncrementExpression)
                            || postfix.IsKind(SyntaxKind.PostDecrementExpression))
                        && SymbolEqualsOriginal(postfixOperand, local):
                    return false;
                case ArgumentSyntax { RefKindKeyword.RawKind: not (int)SyntaxKind.None }
                        refArgument
                    when refArgument.Expression is { } refExpression
                        && SymbolEqualsOriginal(refExpression, local):
                    return false;
            }
        }

        return true;
    }

    private bool SymbolEqualsOriginal(ExpressionSyntax expression, ISymbol symbol)
        => SymbolEqualityComparer.Default.Equals(
            _model.GetSymbolInfo(expression).Symbol, symbol);

    /// <summary>
    /// fixed 初值偏移是否恒为 0：<c>arr</c>（数组标识）或 <c>&amp;arr[0]</c>
    /// （括号/checked 包装剥开；下标须为编译期常量 0）。
    /// </summary>
    private bool HasZeroOffsetFixedInitializer(ExpressionSyntax? rawInitializer)
    {
        if (rawInitializer == null) return false;
        var initializer = UnwrapParenthesized(rawInitializer);
        if (initializer is CheckedExpressionSyntax checkedExpression)
            initializer = UnwrapParenthesized(checkedExpression.Expression);

        if (initializer is IdentifierNameSyntax
            && _model.GetTypeInfo(initializer).Type is IArrayTypeSymbol)
        {
            return true;
        }

        if (initializer is PrefixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.AddressOfExpression,
                    Operand: ElementAccessExpressionSyntax elementAccess
                }
            && elementAccess.ArgumentList.Arguments.Count == 1)
        {
            var indexExpression = elementAccess.ArgumentList.Arguments[0].Expression;
            return _model.GetConstantValue(indexExpression) is { HasValue: true } value
                && value.Value is int intValue
                && intValue == 0;
        }

        return false;
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
    /// <paramref name="sourceSlot"/> 为根的真源源槽键：两个游标仅当源槽相等
    /// 才允许指针比较/相减归一（末端形参按命名约定归基指针槽；R3 fixed
    /// 游标按 fixed 真源归槽；独立形参各自成槽）。
    /// </summary>
    private bool TryResolveIndex(
        ExpressionSyntax raw,
        out ExpressionSyntax array,
        out ExpressionSyntax index,
        out string sourceSlot)
    {
        if (TryBuildIndex(raw, out var arrayText, out var indexNode, out sourceSlot))
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
        out string sourceSlot)
    {
        if (IsFoldsSymbol(symbol))
        {
            var flow = _flowModel.TryGet(symbol, out var f) ? f : default;
            arrayText = flow.ArrayExpressionText;
            index = ParseExpression(flow.OffsetText);
            sourceSlot = flow.SourceSlot ?? flow.ArrayExpressionText;
            return true;
        }

        if (symbol != null && _loweredArrayMap.TryGetValue(symbol, out var lowered))
        {
            arrayText = lowered.ArrayName;
            index = IdentifierName(ownText);
            sourceSlot = lowered.SourceSlot;
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
            sourceSlot = provable.SourceSlot ?? provable.ArrayExpressionText;
            return true;
        }

        // 降级 fallback：符号名本身是某配对指针形参 flow 的 OffsetText
        // （即「R5 配对索引变量」如 pInputIndex）。该符号自身不居中注册
        // （flow 的注册 key 是 pInput 指针符号），但它的名称等于 OffsetText。
        // 此时索引变量自身承载偏移，故 offset 即自身名称。
        // R5 lowered：symbol 可能为 null（pInputIndex 是 R5 新引入节点，
        // 解析器无法映射回原树），此时用 ownText（即标识符文本）作为反查键。
        var offsetLookupName = symbol?.Name ?? ownText;
        if (_flowModel.TryGetByOffsetName(offsetLookupName, out var offsetFlow))
        {
            arrayText = offsetFlow.ArrayExpressionText;
            index = IdentifierName(offsetLookupName);
            sourceSlot = offsetFlow.SourceSlot ?? offsetFlow.ArrayExpressionText;
            return true;
        }

        // 字符串实参的伴生数组来源：C# 的 `string` 本身即不可变 char 序列，配对
        // 形参（declared char*）在 R3 fixed(string) 路径下的既有物化约定就是
        // `expr.ToCharArray()` + 零偏移（见 FixedStatementRewriter 的 InitKind.
        // String）。调用点把 string实参传给指针形参位（多为 string 重载内部
        // 转发给指针重载，或重载解析在受限编译环境下退化到候选符号）时，同
        // 样把实参展开为「物化 char[] + 元素索引 0」，语义与 string 重载的
        // fixed 转发逐位一致（只读使用）。
        if (IsStringTypedSymbol(symbol))
        {
            arrayText = ownText + ".ToCharArray()";
            index = LiteralExpression(
                SyntaxKind.NumericLiteralExpression, Literal(0));
            sourceSlot = PointerMethodCatalog.SymbolSlot(symbol!);
            return true;
        }

        arrayText = string.Empty;
        index = default!;
        sourceSlot = string.Empty;
        return false;
    }

    /// <summary>
    /// 标识符类根符号是否为 <c>string</c> 存储（字段/局部/形参/属性）。配对
    /// 指针形参位的字符串实参据此物化为 `<c>expr.ToCharArray()</c>` + 索引 0。
    /// </summary>
    private static bool IsStringTypedSymbol(ISymbol? symbol) => symbol switch
    {
        ILocalSymbol local => local.Type.SpecialType == SpecialType.System_String,
        IParameterSymbol parameter => parameter.Type.SpecialType == SpecialType.System_String,
        IFieldSymbol field => field.Type.SpecialType == SpecialType.System_String,
        IPropertySymbol property => property.Type.SpecialType == SpecialType.System_String,
        _ => false,
    };

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

            // 标量槽位重解释 *((T*)&scalar)：同尺寸基元位模式归一为
            // System.BitConverter 调用（double↔long、int/uint→float、
            // long/ulong→double），不属于数组索引域。
            if (TryBuildScalarReinterpret(operand, out var scalarReinterpret))
            {
                _statistics.PointerExprsIndexized++;
                return scalarReinterpret;
            }

            // 重解释存储形参上的打包读 *p / *(p±k) / *(T3*)(p±k)：long* 形参
            // 实由 char[] 等异类存储承载时，按字节偏移在存储数组上以小端序
            // 拼出目标基元（catalog 已在计划点限定只读、常量偏移）。
            if (TryBuildReinterpretRead(operand, out var reinterpretRead))
            {
                _statistics.PointerExprsIndexized++;
                return reinterpretRead;
            }

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

    /// <summary>
    /// 标量槽位重解释 <c>*((T*)&amp;scalar)</c>：同尺寸基元位模式经
    /// <c>System.BitConverter</c> 归一（配对匹配见
    /// <see cref="PointerMethodCatalog.TryMatchScalarReinterpret"/>）。
    /// </summary>
    private bool TryBuildScalarReinterpret(
        ExpressionSyntax operand,
        out ExpressionSyntax replacement)
    {
        replacement = null!;

        if (!PointerMethodCatalog.TryMatchScalarReinterpret(
                operand, _model, _resolver,
                out var methodName, out var signedCastKeyword))
        {
            return false;
        }

        var cast = (CastExpressionSyntax)PointerMethodCatalog.UnwrapParentheses(operand);
        var address = (PrefixUnaryExpressionSyntax)
            PointerMethodCatalog.UnwrapParentheses(cast.Expression);
        var scalar = PointerMethodCatalog.UnwrapParentheses(address.Operand);

        ExpressionSyntax argument = scalar;
        if (signedCastKeyword is { } keyword)
        {
            // unchecked((int)x)：位重解释不依赖溢出上下文，显式固定语义。
            argument = CheckedExpression(
                SyntaxKind.UncheckedExpression,
                CastExpression(PredefinedType(Token(keyword)), scalar));
        }

        replacement = InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                ParseExpression("global::System.BitConverter"),
                IdentifierName(methodName)))
            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(argument))));
        return true;
    }

    /// <summary>
    /// 重解释存储形参的只读解引用：形参计划带 <see cref="PairedParamPlan.StorageElement"/>
    /// （声明 T2*、实存 T0[]），解引用形态为 <c>*p</c>、<c>*(p ± k)</c> 或
    /// <c>*(T3*)(p ± k)</c>，按「p 偏移 k 个 T2 = k·sizeof(T2)/sizeof(T0)
    /// 个 T0」换算存储下标，目标 T3 跨 m=sizeof(T3)/sizeof(T0) 个存储元素，
    /// 以小端序拼包：m=1 直接取元素；m&gt;1 在 uint/ulong 持有器中移位或合并。
    /// 有符号存储元素先按存储位宽掩码零扩展；最终按目标类型强转（位模式保持，
    /// 与原 unsafe 小端内存读一致）。
    /// </summary>
    private bool TryBuildReinterpretRead(
        ExpressionSyntax operand,
        out ExpressionSyntax replacement)
    {
        replacement = null!;

        if (_pairedStack.Count == 0)
            return false;

        var plan = _pairedStack[^1];

        var address = UnwrapParenthesized(operand);
        ITypeSymbol? targetElement = null;
        if (address is CastExpressionSyntax addressCast
            && addressCast.Type is PointerTypeSyntax)
        {
            if (GetOldTypeInfo(addressCast).Type is IPointerTypeSymbol castPointer)
                targetElement = castPointer.PointedAtType;
            address = UnwrapParenthesized(addressCast.Expression);
        }

        ExpressionSyntax baseExpression;
        var unitOffset = 0;
        if (address is IdentifierNameSyntax)
        {
            baseExpression = address;
        }
        else if (address is BinaryExpressionSyntax binary
                 && (binary.IsKind(SyntaxKind.AddExpression)
                     || binary.IsKind(SyntaxKind.SubtractExpression))
                 && (UnwrapParenthesized(binary.Left) is IdentifierNameSyntax
                     || UnwrapParenthesized(binary.Right) is IdentifierNameSyntax))
        {
            if (UnwrapParenthesized(binary.Left) is IdentifierNameSyntax leftIdentifier)
            {
                baseExpression = leftIdentifier;
                if (!TryGetConstantInt(binary.Right, out var constant))
                    return false;
                unitOffset = binary.IsKind(SyntaxKind.AddExpression)
                    ? constant
                    : -constant;
            }
            else
            {
                baseExpression = (IdentifierNameSyntax)UnwrapParenthesized(binary.Right);
                if (!TryGetConstantInt(binary.Left, out var constant))
                    return false;
                // n + p 同 p + n；p - n 才可能为负（右侧为标识符时仅 + 成立）。
                unitOffset = binary.IsKind(SyntaxKind.AddExpression)
                    ? constant
                    : -constant;
            }
        }
        else
        {
            return false;
        }

        var baseSymbol = GetExpressionSymbol(baseExpression);
        if (baseSymbol == null)
            return false;

        var slot = plan.Parameters.FirstOrDefault(p =>
            p.Kind == PairedParamKind.PlainPointer
            && p.StorageElement != null
            && SymbolEqualityComparer.Default.Equals(p.Symbol, baseSymbol));
        if (slot is not { Symbol.Type: IPointerTypeSymbol slotPointer, StorageElement: { } storage })
            return false;

        var declaredElement = slotPointer.PointedAtType;
        var readElement = targetElement ?? declaredElement;
        if (!PointerMethodCatalog.TryPrimitiveSize(declaredElement, out var declaredSize)
            || !PointerMethodCatalog.TryPrimitiveSize(storage, out var storageSize)
            || !PointerMethodCatalog.TryPrimitiveSize(readElement, out var targetSize)
            || declaredSize % storageSize != 0
            || targetSize % storageSize != 0)
        {
            return false;
        }

        var storageOffset = unitOffset * (declaredSize / storageSize);
        var elementCount = targetSize / storageSize;
        var arrayName = slot.ArrayName;
        var indexName = slot.IndexName;

        ExpressionSyntax StorageAccess(int elementIndex)
        {
            var absolute = storageOffset + elementIndex;
            ExpressionSyntax index = IdentifierName(indexName);
            if (absolute > 0)
                index = BinaryExpression(
                    SyntaxKind.AddExpression, index, LiteralExpression(
                        SyntaxKind.NumericLiteralExpression, Literal(absolute)));
            else if (absolute < 0)
                index = BinaryExpression(
                    SyntaxKind.SubtractExpression, index, LiteralExpression(
                        SyntaxKind.NumericLiteralExpression, Literal(-absolute)));

            return ElementAccessExpression(IdentifierName(arrayName))
                .WithArgumentList(BracketedArgumentList(
                    SingletonSeparatedList(Argument(index))));
        }

        ExpressionSyntax read;
        if (elementCount == 1)
        {
            read = StorageAccess(0);
        }
        else
        {
            var holder = targetSize <= 4 ? "uint" : "ulong";
            var shiftBits = storageSize * 8;
            var signedStorage = storage.SpecialType is SpecialType.System_SByte
                or SpecialType.System_Int16
                or SpecialType.System_Int32;
            var mask = (1L << shiftBits) - 1;

            ExpressionSyntax? packed = null;
            for (var j = 0; j < elementCount; j++)
            {
                ExpressionSyntax part = CastExpression(
                    ParseTypeName(holder), StorageAccess(j));
                if (signedStorage)
                {
                    part = ParenthesizedExpression(BinaryExpression(
                        SyntaxKind.BitwiseAndExpression, part,
                        CastExpression(ParseTypeName(holder),
                            LiteralExpression(SyntaxKind.NumericLiteralExpression,
                                Literal(mask)))));
                }

                if (j > 0)
                {
                    part = BinaryExpression(
                        SyntaxKind.LeftShiftExpression, part,
                        LiteralExpression(SyntaxKind.NumericLiteralExpression,
                            Literal(j * shiftBits)));
                }

                packed = packed == null
                    ? part
                    : BinaryExpression(SyntaxKind.BitwiseOrExpression, packed, part);
            }

            read = ParenthesizedExpression(packed!);
        }

        if (!SymbolEqualityComparer.Default.Equals(readElement, storage))
        {
            read = CastExpression(ParseTypeName(readElement.ToDisplayString()), read);
        }

        replacement = read;
        return true;
    }

    private bool TryGetConstantInt(ExpressionSyntax expression, out int value)
    {
        value = 0;
        var original = _resolver.Resolve(expression) as ExpressionSyntax;
        var constant = _model.GetConstantValue(original ?? expression);
        if (constant.HasValue && constant.Value is int intValue)
        {
            value = intValue;
            return true;
        }

        return false;
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

        // 旧模型两侧均非指针（二次跑的 int 产物、R3 已降级且溯源为 int 的
        // 运算、普通 int 比较）：普通托管二元，子节点各自归一即可。
        // R3 降级但本 pass 旧类型仍为指针的比较/相减不在此列——必须继续下探
        // 过同源槽门：分别 fixed 在两个不同数组上的游标降级后文本上已是 int
        // 比较，直接放行会把跨数组索引误当地址序，不同源须保守 UF020。

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
        out string sourceSlot)
    {
        var e = UnwrapParenthesized(raw);
        arrayText = string.Empty;
        index = default!;
        sourceSlot = string.Empty;

        switch (e)
        {
            case IdentifierNameSyntax:
                var idSymbol = GetExpressionSymbol(e);
                // R5 lowered fallback: GetExpressionSymbol 对新引入的配对索引标识符
                // （如 pInputIndex）返回 null，但仍需走 TryResolveIndexSymbol，
                // 其内部会拿 ownText 与 flow 的 OffsetText 比对。不能因为 symbol
                // 为 null 就直接放弃。
                return TryResolveIndexSymbol(
                    idSymbol, e.ToString(),
                    out arrayText, out index, out sourceSlot);

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
                        sourceSlot = holderArray;
                        return true;
                    }
                }

                if (GetExpressionSymbol(e) is { } memberSymbol)
                {
                    return TryResolveIndexSymbol(
                        memberSymbol, e.ToString(),
                        out arrayText, out index, out sourceSlot);
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
                            out var nestedSlot))
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
                    sourceSlot = nestedSlot;
                    _statistics.PointerArithmeticPropagated++;
                    return true;
                }

                if (binary.Kind() == SyntaxKind.SubtractExpression
                    && bLeftPointer && !bRightPointer)
                {
                    if (!TryBuildIndex(binary.Left, out var nestedArray, out var nestedIndex,
                            out var nestedSlot))
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
                    sourceSlot = nestedSlot;
                    _statistics.PointerArithmeticPropagated++;
                    return true;
                }

                // 降级 fallback：两边均已降为 int（如 pInputIndex + next），但某
                // 边的标识符是配对指针形参的 index 变量（OffsetText 命中）。
                // 以该 index 变量为 cursor，另一侧为算术偏移。
                if (binary.Kind() == SyntaxKind.AddExpression && !bLeftPointer && !bRightPointer)
                {
                    ExpressionSyntax indexSide;
                    ExpressionSyntax offsetSide;
                    if (IsPairedIndexIdentifier(binary.Left))
                    {
                        indexSide = binary.Left;
                        offsetSide = binary.Right;
                    }
                    else if (IsPairedIndexIdentifier(binary.Right))
                    {
                        indexSide = binary.Right;
                        offsetSide = binary.Left;
                    }
                    else
                    {
                        return false;
                    }

                    if (!TryBuildIndex(indexSide, out var nestedArray, out var nestedIndex,
                            out var nestedSlot))
                    {
                        return false;
                    }

                    var visitedOffsetSide = (ExpressionSyntax)Visit(offsetSide)!;
                    var combined = Simplify(BinaryExpression(
                        SyntaxKind.AddExpression,
                        ParenthesizedExpression(nestedIndex),
                        ParenthesizedExpression(visitedOffsetSide)));

                    arrayText = nestedArray;
                    index = combined;
                    sourceSlot = nestedSlot;
                    _statistics.PointerArithmeticPropagated++;
                    return true;
                }

                // 指针差值/比较不是「单个数组+索引」形态，由调用方专门处理。
                return false;

            case ConditionalExpressionSyntax conditional:
                if (TryBuildIndex(conditional.WhenTrue, out var trueArray, out var trueIndex,
                        out var trueSlot)
                    && TryBuildIndex(conditional.WhenFalse, out var falseArray, out var falseIndex,
                        out var falseSlot)
                    && trueArray == falseArray
                    && trueSlot == falseSlot)
                {
                    arrayText = trueArray;
                    index = ConditionalExpression(
                        conditional.Condition,
                        ParenthesizedExpression(trueIndex),
                        ParenthesizedExpression(falseIndex));
                    sourceSlot = trueSlot;
                    return true;
                }

                return false;

            case CheckedExpressionSyntax checkedExpression:
                return TryBuildIndex(checkedExpression.Expression,
                    out arrayText, out index, out sourceSlot);

            case PostfixUnaryExpressionSyntax postfix
                when postfix.Kind() is SyntaxKind.PostIncrementExpression
                    or SyntaxKind.PostDecrementExpression:
                return TryBuildMutationIndex(postfix.Operand, postfix,
                    out arrayText, out index, out sourceSlot);

            case PrefixUnaryExpressionSyntax prefix
                when prefix.Kind() is SyntaxKind.PreIncrementExpression
                    or SyntaxKind.PreDecrementExpression:
                return TryBuildMutationIndex(prefix.Operand, prefix,
                    out arrayText, out index, out sourceSlot);

            case InvocationExpressionSyntax invocation
                when TryGetInvocationPlan(invocation) is { } calleePlan:
                var rebuilt = ExpandPairedInvocation(
                    invocation, calleePlan, out var firstArray, out var firstSlot);
                if (firstArray == null) return false;
                arrayText = firstArray;
                index = rebuilt;
                sourceSlot = firstSlot ?? firstArray;
                return true;

            case CastExpressionSyntax cast
                when cast.Expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.SubtractExpression } innerDifference
                    && IsOldPointerTyped(innerDifference.Left):
                // 嵌套 (T)(p-q)：内层差值构造。
                return TryBuildIndex(cast.Expression, out arrayText, out index, out sourceSlot);

            default:
                return false;
        }
    }

    private bool TryBuildMutationIndex(
        ExpressionSyntax rawTarget,
        SyntaxNode mutation,
        out string arrayText,
        out ExpressionSyntax index,
        out string sourceSlot)
    {
        var target = UnwrapParenthesized(rawTarget);
        var symbol = GetExpressionSymbol(target);

        // 配对折叠游标：自增直接作用在配对索引变量自身。
        if (IsFoldsSymbol(symbol))
        {
            var foldsFlow = _flowModel.TryGet(symbol!, out var f) ? f : default;
            arrayText = foldsFlow.ArrayExpressionText;
            index = BuildMutationExpression(foldsFlow.OffsetText, mutation.Kind());
            sourceSlot = foldsFlow.SourceSlot ?? foldsFlow.ArrayExpressionText;
            _statistics.PointerIncrements++;
            return true;
        }

        // R3 降级游标：标识符本身即 int 索引局部，原地自增。
        if (symbol != null && _loweredArrayMap.TryGetValue(symbol, out var lowered))
        {
            arrayText = lowered.ArrayName;
            index = BuildMutationExpression(target.ToString(), mutation.Kind());
            sourceSlot = lowered.SourceSlot;
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
                sourceSlot = string.Empty;
                return false;
            }

            arrayText = flow.ArrayExpressionText;
            index = BuildMutationExpression(local.Name, mutation.Kind());
            sourceSlot = flow.SourceSlot ?? flow.ArrayExpressionText;
            _statistics.PointerIncrements++;
            return true;
        }

        arrayText = default!;
        index = default!;
        sourceSlot = string.Empty;
        return false;
    }

    private enum DifferenceReason { SameSource, DifferentSource, Unprovable }

    private bool TryBuildIndexDifference(
        BinaryExpressionSyntax node,
        out DifferenceReason reason,
        out ExpressionSyntax diffIndex)
    {
        diffIndex = default!;

        var leftOk = TryBuildIndex(node.Left, out _, out var leftIndex,
            out var leftSlot);
        var rightOk = TryBuildIndex(node.Right, out _, out var rightIndex,
            out var rightSlot);

        if (!leftOk || !rightOk)
        {
            reason = DifferenceReason.Unprovable;
            return false;
        }

        // 同源门以真源源槽身份判定：独立指针形参各自成槽；末端形参
        // （pEnd 命名约定）归属基指针槽；R3 fixed 游标按 fixed 真源归槽。
        // 槽不等即不同数组空间，相减无意义——保守 UF020，不做配对放宽。
        if (leftSlot != rightSlot)
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

        if (!TryBuildIndex(node.Left, out _, out var leftIndex, out var leftSlot)
            || !TryBuildIndex(node.Right, out _, out var rightIndex, out var rightSlot))
        {
            return false;
        }

        // 同源门：仅真源源槽相等的游标比较才归一为 int 索引比较。
        if (leftSlot != rightSlot)
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
                var rebuiltRight = ExpandPairedInvocation(rhsInvocation, rhsPlan, out _, out _);
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
            // 信号⑧ 配对方法域：可证偏移实参（pIndex ± n 等）外发给非配对
            // 调用（含元数据/extern，如 Encoding.UTF8.GetBytes(pInput+next)）
            // 归一为 int 索引表达式，指针不逃逸；根不可证（异源/外部原生源）
            // 仍保守 UF020。裸形参实参已在上方 flow 分支按数组名保留。
            if (_pairedStack.Count > 0
                && TryBuildIndex(node.Expression, out _, out var argumentIndex, out _))
            {
                _statistics.PointerExprsIndexized++;
                return node.WithExpression(Simplify(argumentIndex));
            }

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
