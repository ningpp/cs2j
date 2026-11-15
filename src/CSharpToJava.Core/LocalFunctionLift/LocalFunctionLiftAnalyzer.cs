using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Globalization;
using System.Linq;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Pure analysis pass: locates host types, determines static, collects captures,
/// runs the fixpoint propagation, and evaluates all skip gates.
/// Produces a list of LiftPlan objects for the rewriter.
/// </summary>
internal sealed class LocalFunctionLiftAnalyzer
{
    private readonly SemanticModel _model;
    private readonly LocalFunctionLiftOptions _options;
    private readonly LocalFunctionLiftStatistics _stats;
    private readonly List<LocalFunctionLiftDiagnostic> _diagnostics;
    private readonly Dictionary<string, HashSet<string>> _crossFileNameTable;
    /// <summary>条件编译区（本次分析编译未启用对应符号）里以函数式引用出现的标识符。</summary>
    private readonly Dictionary<string, List<DisabledTextReference>> _disabledTextReferences =
        new(StringComparer.Ordinal);
    /// <summary>本文件里同名局部函数的数量（改名时判断条件编译区引用是否唯一指向某个 plan）。</summary>
    private Dictionary<string, int> _localFunctionNameCounts = new(StringComparer.Ordinal);

    /// <summary>本文件里所有条件编译区（禁用文本）的原文，用于改名前的精确判定。</summary>
    private readonly List<string> _disabledTextTexts = new();

    /// <summary>本文件里以方法组 / 委托转换形式被引用的方法符号（含构造泛型的原定义）。</summary>
    private HashSet<ISymbol> _methodGroupSymbols = new(SymbolEqualityComparer.Default);

    /// <summary>本文件的条件编译指令布局（判定顶层局部函数的声明区）。</summary>
    private ConditionalDirectiveLayout? _conditionalLayout;

    /// <summary>顶层语句宿主的合成静态类名（全文件共用，冲突时加数字后缀）。</summary>
    private string? _topLevelHostClassName;

    /// <summary>条件编译区里的一个函数式引用在文件中的位置。</summary>
    private readonly record struct DisabledTextReference(
        int TriviaPosition, int Offset, int Length, TextSpan EnclosingMemberSpan);

    public LocalFunctionLiftAnalyzer(
        SemanticModel model,
        LocalFunctionLiftOptions options,
        LocalFunctionLiftStatistics stats,
        List<LocalFunctionLiftDiagnostic> diagnostics,
        Dictionary<string, HashSet<string>> crossFileNameTable)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _crossFileNameTable = crossFileNameTable;
    }

    /// <summary>
    /// Analyzes all local functions in the compilation unit and returns LiftPlans.
    /// </summary>
    public List<LiftPlan> Analyze(CompilationUnitSyntax root)
    {
        var localFns = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToList();
        var plans = new List<LiftPlan>();

        _localFunctionNameCounts = localFns
            .GroupBy(static f => f.Identifier.Text, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.Ordinal);
        CollectNamesReferencedInDisabledText(root);
        _methodGroupSymbols = CollectMethodGroupSymbols(root);

        foreach (var localFn in localFns)
        {
            var plan = AnalyzeOne(root, localFn);
            plans.Add(plan);
        }

        PropagateCaptures(plans);

        var bySymbol = plans.Where(p => p.Symbol != null)
            .ToDictionary(p => p.Symbol!, p => p, SymbolEqualityComparer.Default);

        // 写入型捕获要进 lambda（方法组转换 / 已有 lambda 内引用）时不能是 ref 形参
        // （CS1628）。Holder 决策按变量符号全局生效：一旦某个变量改成 StrongBox，
        // 所有提升函数对它的捕获都必须统一成 StrongBox 形参。
        var holderSymbols = CollectHolderCaptureSymbols(plans, bySymbol);
        ApplyHolderCaptures(plans, holderSymbols);

        var activePlans = plans.Where(p => !p.Skipped).ToList();
        foreach (var plan in plans)
        {
            if (plan.Skipped)
                continue;
            CheckPostPropagationGates(plan, bySymbol, activePlans);
        }

        // Gate #12: References skipped local functions.
        // If a non-skipped local function calls a skipped local function,
        // it cannot be lifted because the skipped function remains local.
        CheckReferencesToSkippedLocalFunctions(plans);
        CheckDuplicateNamesWithUnboundCallSites(root, plans);

        return plans;
    }

    /// <summary>
    /// 同一宿主类型内的重名局部函数：不再因为「存在未绑定调用点」整组跳过。
    /// C# 的局部函数遵循词法作用域——不同方法/语句容器里的同名声明彼此不可见，
    /// 任何调用点只能看到其最内层语句容器里的那一个（同容器内则是重载）。
    /// 这里用共享解析器逐条预校验未绑定引用：只有解析器（词法作用域 + 重载消歧）
    /// 确实无法归属的引用，才会让其最内层作用域里的 plan 保留原样并给出诊断；
    /// 其余 plan 正常提升。
    /// </summary>
    private void CheckDuplicateNamesWithUnboundCallSites(
        CompilationUnitSyntax root, List<LiftPlan> plans)
    {
        var activePlans = plans.Where(p => !p.Skipped).ToList();
        var bySymbol = activePlans
            .Where(p => p.Symbol != null)
            .ToDictionary(p => p.Symbol!, p => p, SymbolEqualityComparer.Default);

        foreach (var group in activePlans
                     .GroupBy(p => (Host: p.HostType, Name: p.Node.Identifier.Text)))
        {
            if (group.Count() < 2)
                continue;

            var name = group.Key.Name;
            var plansInGroup = group.ToList();
            var unresolved = new List<SyntaxNode>();
            foreach (var reference in EnumerateUnboundReferences(root, name))
            {
                var resolved = LocalFunctionCallSiteResolver.Resolve(
                    reference, name, activePlans, _model, bySymbol);
                if (resolved == null)
                    unresolved.Add(reference);
            }

            if (unresolved.Count == 0)
                continue;

            foreach (var plan in plansInGroup)
            {
                if (!unresolved.Any(reference => IsInInnermostScope(reference, plan, activePlans)))
                    continue;

                Skip(plan, LocalFunctionSkipReason.AmbiguousName,
                    "a call site cannot be uniquely attributed by lexical scope and overload " +
                    "resolution; the name-based fallback would target the wrong overload");
            }
        }
    }

    /// <summary>
    /// 名字匹配且符号绑定失败的调用/方法组引用节点。排除命名实参名与成员访问的
    /// Name 部分（那些不是本作用域的局部函数引用）。
    /// </summary>
    private IEnumerable<SyntaxNode> EnumerateUnboundReferences(SyntaxNode root, string name)
    {
        foreach (var node in root.DescendantNodes())
        {
            string? nodeName = node switch
            {
                InvocationExpressionSyntax { Expression: IdentifierNameSyntax id } => id.Identifier.Text,
                InvocationExpressionSyntax { Expression: GenericNameSyntax gn } => gn.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                GenericNameSyntax gn => gn.Identifier.Text,
                _ => null,
            };
            if (!string.Equals(nodeName, name, StringComparison.Ordinal))
                continue;

            if (node.Parent is NameColonSyntax)
                continue;
            if (node.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name == node)
                continue;

            if (_model.GetSymbolInfo(node).Symbol != null)
                continue;

            yield return node;
        }
    }

    /// <summary>
    /// 引用是否落在 plan 的「最内层同名语句容器」内：从引用向上遇到的第一个
    /// 含同名局部函数声明的语句容器，就是 C# 遮蔽规则下的唯一候选容器。
    /// </summary>
    private static bool IsInInnermostScope(
        SyntaxNode reference,
        LiftPlan plan,
        IReadOnlyList<LiftPlan> activePlans)
    {
        var name = plan.Node.Identifier.Text;
        var ancestors = new HashSet<SyntaxNode>();
        for (var current = reference.Parent; current != null; current = current.Parent)
            ancestors.Add(current);

        // 找出引用可见范围内最深的同名声明容器。
        SyntaxNode? innermost = null;
        var innermostDepth = -1;
        foreach (var candidate in activePlans)
        {
            if (!string.Equals(candidate.Node.Identifier.Text, name, StringComparison.Ordinal))
                continue;
            var container = LocalFunctionCallSiteResolver.GetStatementContainer(candidate.Node);
            if (container == null || !ancestors.Contains(container))
                continue;

            var depth = 0;
            for (var current = container; current != null; current = current.Parent)
                depth++;
            if (depth > innermostDepth)
            {
                innermost = container;
                innermostDepth = depth;
            }
        }

        return innermost != null
            && ReferenceEquals(LocalFunctionCallSiteResolver.GetStatementContainer(plan.Node), innermost);
    }

    /// <summary>
    /// 收集本文件里以方法组 / 委托转换形式出现的引用所绑定到的符号。
    /// 调用目标、成员访问名、命名实参和 <c>nameof</c> 实参都不是方法组转换。
    /// 显式类型实参形式（<c>Gen&lt;int&gt;</c>）同样是方法组转换，必须一并识别，
    /// 否则「own 类型参数 + carried 外层类型参数」的保守跳过会漏掉这类引用。
    /// </summary>
    private HashSet<ISymbol> CollectMethodGroupSymbols(CompilationUnitSyntax root)
    {
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Parent is InvocationExpressionSyntax invocation && invocation.Expression == id)
                continue;
            if (id.Parent is GenericNameSyntax)
                continue;
            if (id.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == id)
                continue;
            if (id.Parent is NameColonSyntax)
                continue;
            if (CaptureClassification.IsInsideNameOf(id))
                continue;

            if (_model.GetSymbolInfo(id).Symbol is IMethodSymbol method)
            {
                symbols.Add(method);
                symbols.Add(method.OriginalDefinition);
            }
        }

        foreach (var generic in root.DescendantNodes().OfType<GenericNameSyntax>())
        {
            if (generic.Parent is InvocationExpressionSyntax invocation
                && invocation.Expression == generic)
                continue;
            if (generic.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name == generic)
                continue;
            if (generic.Parent is NameColonSyntax)
                continue;
            if (CaptureClassification.IsInsideNameOf(generic))
                continue;

            if (_model.GetSymbolInfo(generic).Symbol is IMethodSymbol method)
            {
                symbols.Add(method);
                symbols.Add(method.OriginalDefinition);
            }
        }
        return symbols;
    }

    private LiftPlan AnalyzeOne(CompilationUnitSyntax root, LocalFunctionStatementSyntax localFn)
    {
        var symbol = _model.GetDeclaredSymbol(localFn) as IMethodSymbol;
        var plan = new LiftPlan
        {
            Node = localFn,
            Symbol = symbol!,
            NewName = "",
            IsStatic = false,
            Skipped = false,
            SkipReason = LocalFunctionSkipReason.None,
        };

        // Gate #2: No body
        if (localFn.Body == null && localFn.ExpressionBody == null)
        {
            Skip(plan, LocalFunctionSkipReason.NoBody, "local function has no body");
            return plan;
        }

        // 名字在条件编译区（如 #if DEBUG）里被引用：禁用区是不透明文本，重写器只能
        // 改活动代码。提升方法保留原名即可让两种符号配置下的引用都解析到同一个方法
        // （cs-text p05: RegexPrefixAnalyzer 的 `nameof(TryFindRawFixedSets)` 位于
        // #if DEBUG，generator 工程真实编译时 DEBUG 是定义的）。原名与宿主类型成员
        // 冲突时改由 AssignName 做条件编译区内的精确替换，或带原因跳过。
        if (_disabledTextReferences.ContainsKey(localFn.Identifier.Text))
        {
            plan.PreserveOriginalName = true;
        }

        // Gate #1 (disabled): Iterator (yield return / yield break).
        // Iterator local functions can be lifted to class-level methods
        // that also use yield return. The hoisted method will be a valid
        // iterator method.
        // Note: iterator methods cannot have ref/out parameters (CS1623), so
        // write captures are converted to StrongBox holders when possible and
        // otherwise skipped by the post-propagation gate.

        // Gate #3: No host type. 顶层语句没有语法宿主类型，但可以合成一个文件内的
        // 静态类承载提升方法（调用点也在同一文件的顶层语句里，internal 可见）。
        var hostType = FindHostType(localFn);
        var isTopLevelHost = false;
        var initializerContext = InitializerContext.None;
        if (hostType == null)
        {
            if (!IsInTopLevelStatements(localFn))
            {
                Skip(plan, LocalFunctionSkipReason.NoHostType, "no enclosing type declaration found");
                return plan;
            }

            // 声明点位于条件编译区（如 #if DEBUG）：合成类的方法无法安全落进同一
            // 分支，本轮保守跳过并给出精确诊断（同文件其它无条件区局部函数照常提升）。
            _conditionalLayout ??= ConditionalDirectiveLayout.Create(root.SyntaxTree);
            if (_conditionalLayout.StackAt(localFn.SpanStart).Count > 0)
            {
                Skip(plan, LocalFunctionSkipReason.ConditionalTopLevelDeclaration,
                    "top-level local function is declared inside a conditional compilation region; " +
                    "the generated method could not be placed in the same branch");
                return plan;
            }

            isTopLevelHost = true;
            plan.IsTopLevelHost = true;
            plan.TopLevelHostClassName = EnsureTopLevelHostClassName(root);
        }
        else
        {
            // 字段/属性初始化器里唯一合法的局部函数形态是「初始化器 → lambda → 局部函数」：
            // 宿主类型存在，调用点在 lambda 体内（VisitBlock 的移除通道可用），捕获
            // （含 lambda 形参/局部）按既有机制变成提升方法形参，因此可以提升。不在
            // lambda 内却落在初始化器里的形态在合法 C# 中不存在，保留兜底跳过。
            initializerContext = GetInitializerContext(localFn);
            if (initializerContext != InitializerContext.None
                && !IsInsideAnonymousFunction(localFn, hostType))
            {
                Skip(plan, LocalFunctionSkipReason.NoHostType,
                    "local function is inside a field/property initializer without an enclosing lambda");
                return plan;
            }
            plan.HostType = hostType;

            // Gate #4: Interface host
            // 接口里的局部函数只可能位于默认接口方法（DIM，C# 8+）体内。提升后的
            // 方法以 `private` 修饰符落回同一接口声明：C# 8 起接口允许带体的私有
            // 实例方法，调用点（DIM 体内）可以看到它。
            if (hostType is InterfaceDeclarationSyntax)
            {
                if (!_options.AllowInterfaceHost)
                {
                    Skip(plan, LocalFunctionSkipReason.InterfaceHost, "host type is an interface");
                    return plan;
                }

                // 语言版本守卫：接口方法体本身要求 C# 8+。项目以更低版本解析时，
                // 提升出的 private 接口方法（带体）也不合法，必须保持原样并给出
                // 精确诊断，而不是生成必然编译失败的代码。
                var languageVersion = GetLanguageVersion();
                if (!SupportsInterfaceMethodBody(languageVersion))
                {
                    Skip(plan, LocalFunctionSkipReason.InterfaceHost,
                        $"interface method bodies require C# 8 or later, but the project parses with " +
                        $"LanguageVersion {languageVersion}; the lifted private interface method would " +
                        "not be legal");
                    return plan;
                }
            }
        }

        // Gate #6 (supported): checked/unchecked context no longer skips the lift.
        // 提升方法体整体包一层同名块后，算术溢出检查语义与声明点完全一致
        // （体内显式的 checked/unchecked 表达式仍然局部覆盖）。
        plan.CheckedContext = GetCheckedContext(localFn);

        // 顶层语句等价于静态 Main：提升方法一律 static（源里也没有 this 可用）。
        plan.IsStatic = isTopLevelHost || DetermineStatic(localFn);
        // 初始化器的值没有 this 上下文：提升方法若写成实例方法，初始化器 lambda
        // 里的调用会因隐式 this 触发 CS0236。这里一律 static（实例初始化器本来
        // 也引用不到实例成员）。属性表达式体是 getter，仍按属性自身的修饰符判定。
        if (!isTopLevelHost && initializerContext == InitializerContext.InitializerValue)
            plan.IsStatic = true;
        var isExplicitStatic = IsExplicitStaticLocalFunction(localFn);

        // static 局部函数不可能真正捕获外层局部（编译器已保证，CS8421），因此不存在
        // 「实例方法里的 static 局部函数引用外层局部 → 跳过」的分支。但 nameof(外层
        // 局部/形参) 是合法的编译期名字引用：提升到类级方法后该名字不可见（CS0103），
        // 仍需按非 static 收集捕获，让重写器把 nameof 折叠成原名字符串
        // （cs-text p05: NumberFormatInfo.ThrowInvalid 的 nameof(style)）。
        if (isExplicitStatic && ReferencesEnclosingLocals(localFn, symbol!))
            isExplicitStatic = false;

        // Gate #5 (disabled): Method group / delegate conversion reference.
        // We now support this case — the rewriter will rename the method group
        // reference to the new method name. The delegate conversion still works
        // because the hoisted method has the same signature.

        // Collect captures
        var unresolvedCaptures = new List<ISymbol>();
        plan.Captures = CollectCaptures(
            localFn, symbol!, isExplicitStatic, hostType, unresolvedCaptures);

        // 捕获变量的类型在语义模型里是 ErrorType，且声明处也提取不到可解析的类型语法
        // （显式类型或 var 初始化表达式都不是对象创建/cast/default/数组创建）时，
        // 无法拼出形参类型。若继续提升，方法体仍按原名引用该变量，生成的方法作用域里
        // 没有这个名字（CS0103）。保持 skip + 诊断：strict 模式下这正是必须暴露的
        // 残留局部函数。
        if (unresolvedCaptures.Count > 0)
        {
            var names = string.Join(", ", unresolvedCaptures
                .Select(static s => s.Name)
                .Distinct(StringComparer.Ordinal));
            Skip(plan, LocalFunctionSkipReason.UnsupportedCaptureType,
                $"captured variable(s) {names} have unresolved types; the hoisted method " +
                "cannot declare a parameter for them and would reference undeclared names");
            return plan;
        }

        // 外层 const 局部是编译期常量，不是捕获：常量语境（is 模式、参数默认值、
        // 特性实参、goto case、switch 标签）改写成捕获形参会 CS0103/CS0150。
        // 提升后按值内联成字面量，不生成形参、调用点不加实参。static 局部函数
        // 也允许引用常量，故不受 isStatic 影响。
        var unresolvedConstants = new List<string>();
        plan.InlinedConstants = CollectInlinedConstants(
            localFn, symbol!, hostType, unresolvedConstants);
        if (unresolvedConstants.Count > 0)
        {
            Skip(plan, LocalFunctionSkipReason.UnsupportedCaptureType,
                $"referenced const local(s) {string.Join(", ", unresolvedConstants)} " +
                "have no usable constant value and cannot be inlined as literals");
            return plan;
        }

        // Gate #7: Unsupported capture type
        foreach (var cap in plan.Captures)
        {
            if (IsUnsupportedCaptureType(cap))
            {
                Skip(plan, LocalFunctionSkipReason.UnsupportedCaptureType,
                    $"capture variable '{cap.OriginalName}' has unsupported type: {cap.TypeDisplay}");
                return plan;
            }
        }

        // Assign name
        plan.NewName = AssignName(plan, localFn, hostType, symbol!);
        if (plan.Skipped)
            return plan;

        // Gate #10: CapturesAtEnd with params/optional
        if (_options.CapturesAtEnd)
        {
            if (HasParamsOrOptional(localFn))
            {
                Skip(plan, LocalFunctionSkipReason.ParamsOrOptionalConflict,
                    "CapturesAtEnd=true but original parameters contain params or default values");
                return plan;
            }
        }

        // Gate #11 (removed): Named argument at call sites.
        // Named arguments still work after lifting because capture parameters
        // are prepended as positional arguments, and named arguments reference
        // the original parameter names which remain unchanged. C# allows
        // mixing positional and named arguments as long as positionals come first.

        // R1b：类型参数携带（own + 外层方法/局部函数约束闭包、宿主类级形参不重复声明）
        // 交给统一解析器 SyntheticTypeParameterResolver；同名遮蔽由 StableName 消歧。
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        var signatureTypes = new List<ITypeSymbol?>(localFn.ParameterList.Parameters.Count + 1);
        foreach (var parameter in localFn.ParameterList.Parameters)
            signatureTypes.Add((_model.GetDeclaredSymbol(parameter) as IParameterSymbol)?.Type);
        signatureTypes.Add(symbol!.ReturnType);

        var typeParameterPlan = SyntheticTypeParameterResolver.Resolve(
            _model, localFn,
            ownTypeParameterList: localFn.TypeParameterList,
            ownConstraintClauses: localFn.ConstraintClauses,
            body: body,
            signatureTypes: signatureTypes);

        // S-03：任一携带形参无法安全表达 → 保守跳过（不产出悬挂 where 子句）。
        if (typeParameterPlan.Parameters.Any(static p =>
                p.State == SyntheticTypeParameterState.NotExpressible))
        {
            var reasons = string.Join("; ", typeParameterPlan.Diagnostics
                .Where(static d => d.Code == SyntheticTypeParameterResolver.NotExpressibleCode)
                .Select(static d => d.Message));
            Skip(plan, LocalFunctionSkipReason.EnclosingTypeParameterConflict,
                $"type parameter constraints cannot be reproduced safely: {reasons}");
            return plan;
        }

        plan.TypeParameterPlan = typeParameterPlan;
        var carriedEnclosing = typeParameterPlan.Parameters
            .Where(static p => p.Origin != SyntheticTypeParameterOrigin.Own)
            .Select(static p => p.RenderName)
            .ToList();
        if (carriedEnclosing.Count > 0)
        {
            plan.EnclosingTypeParams = carriedEnclosing;
            plan.NeedsExplicitTypeArguments = true;
        }

        // 提升方法的类型形参表是 own + carried：调用点必须显式写出全部实参。
        // 普通调用点还能按名书写局部函数自己的类型参数（Local<int>(...)）；方法组
        // 转换则完全无法携带显式类型实参，重写器只能把它改写成 lambda，而 lambda
        // 里的调用无从命名 own 类型参数 → 保守跳过，避免产出 CS0123/CS0305。
        if (plan.NeedsExplicitTypeArguments
            && plan.Symbol != null
            && localFn.TypeParameterList is { Parameters.Count: > 0 }
            && _methodGroupSymbols.Contains(plan.Symbol))
        {
            Skip(plan, LocalFunctionSkipReason.EnclosingTypeParameterConflict,
                "method group conversion cannot supply the local function's own type arguments " +
                "explicitly (the hoisted method declares them plus carried enclosing type parameters)");
            return plan;
        }

        return plan;
    }

    private void Skip(LiftPlan plan, LocalFunctionSkipReason reason, string message)
    {
        plan.Skipped = true;
        plan.SkipReason = reason;
        _stats.LocalFunctionsSkipped++;
        if (!_stats.SkippedByReason.TryAdd(reason, 1))
            _stats.SkippedByReason[reason]++;

        var lineSpan = plan.Node.GetLocation().GetLineSpan();
        var line = lineSpan.IsValid ? lineSpan.StartLinePosition.Line + 1 : 0;
        _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Warning,
            $"Skipped local function '{plan.Node.Identifier.Text}': {message}",
            null, line, reason));
    }

    /// <summary>
    /// 本次分析所使用的 C# 语言版本。语法树不是 C# 树（不应发生）时按
    /// <see cref="LanguageVersion.Default"/> 处理，即「最新主版本」。
    /// </summary>
    private LanguageVersion GetLanguageVersion()
        => (_model.SyntaxTree.Options as CSharpParseOptions)?.LanguageVersion
            ?? LanguageVersion.Default;

    /// <summary>
    /// 接口方法体（默认接口方法、静态/私有带体方法）自 C# 8 起才合法。
    /// <see cref="LanguageVersion.Default"/> / <see cref="LanguageVersion.Latest"/> /
    /// <see cref="LanguageVersion.Preview"/> 都映射到最新主版本，视为支持。
    /// </summary>
    private static bool SupportsInterfaceMethodBody(LanguageVersion version)
        => version switch
        {
            LanguageVersion.Default or LanguageVersion.Latest or LanguageVersion.Preview => true,
            _ => version >= LanguageVersion.CSharp8,
        };

    private static TypeDeclarationSyntax? FindHostType(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax typeDecl)
                return typeDecl;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// 局部函数是否声明在顶层语句里（<see cref="GlobalStatementSyntax"/> 直接挂在
    /// <see cref="CompilationUnitSyntax"/> 下）。顶层语句没有语法宿主类型，但等价于
    /// 静态 Main，可以提升到文件内合成的静态类。
    /// </summary>
    private static bool IsInTopLevelStatements(LocalFunctionStatementSyntax localFn)
    {
        for (var current = localFn.Parent; current != null; current = current.Parent)
        {
            if (current is GlobalStatementSyntax { Parent: CompilationUnitSyntax })
                return true;
            if (current is TypeDeclarationSyntax or BaseMethodDeclarationSyntax)
                return false;
        }
        return false;
    }

    /// <summary>
    /// 顶层宿主合成类名：<c>__Cs2jLocalFunctions</c>。必须避开本文件已声明的类型名、
    /// 顶层变量名，以及编译单元内按简单名可见的类型/命名空间（同名会遮蔽引用），
    /// 冲突时追加 2、3…
    /// </summary>
    private string EnsureTopLevelHostClassName(CompilationUnitSyntax root)
    {
        if (_topLevelHostClassName != null)
            return _topLevelHostClassName;

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    used.Add(type.Identifier.Text);
                    break;
                case DelegateDeclarationSyntax @delegate:
                    used.Add(@delegate.Identifier.Text);
                    break;
                case VariableDeclaratorSyntax declarator
                    when declarator.Ancestors().Any(static ancestor =>
                        ancestor is GlobalStatementSyntax):
                    used.Add(declarator.Identifier.Text);
                    break;
            }
        }

        var name = LocalFunctionLiftNaming.ResolveUniqueName("__Cs2jLocalFunctions", used);
        while (_model.LookupNamespacesAndTypes(root.SpanStart, name: name).Any()
               || _model.LookupSymbols(root.SpanStart, name: name).Any())
        {
            name = LocalFunctionLiftNaming.ResolveUniqueName("__Cs2jLocalFunctions", used);
        }

        _topLevelHostClassName = name;
        return name;
    }

    /// <summary>局部函数声明点所处的初始化器形态。</summary>
    private enum InitializerContext
    {
        /// <summary>不在字段/属性初始化器或属性表达式体内。</summary>
        None,

        /// <summary>字段/属性初始化器的值：没有可用的 <c>this</c>（CS0236）。</summary>
        InitializerValue,

        /// <summary>属性表达式体（getter）：<c>this</c> 可用。</summary>
        PropertyExpressionBody,
    }

    /// <summary>
    /// C# 不允许在字段/属性初始化器或其表达式体里直接声明局部函数，唯一合法形态
    /// 是「初始化器 → lambda → 局部函数」。这里区分两种宿主形态：初始化器的值
    /// 没有 <c>this</c> 上下文（初始化器里引用实例成员，包括 lambda 体内引用
    /// 提升后的实例方法，都是 CS0236），而属性表达式体是 getter。
    /// </summary>
    private static InitializerContext GetInitializerContext(SyntaxNode localFn)
    {
        for (var current = localFn.Parent; current != null; current = current.Parent)
        {
            if (current is VariableDeclaratorSyntax declarator
                && declarator.Initializer != null
                && declarator.Parent?.Parent is FieldDeclarationSyntax)
            {
                return InitializerContext.InitializerValue;
            }

            if (current is PropertyDeclarationSyntax property)
            {
                if (property.Initializer != null)
                    return InitializerContext.InitializerValue;
                if (property.ExpressionBody != null)
                    return InitializerContext.PropertyExpressionBody;
            }

            if (current is TypeDeclarationSyntax)
                return InitializerContext.None;
        }

        return InitializerContext.None;
    }

    /// <summary>
    /// 局部函数声明点所处的 checked/unchecked 上下文：只看声明点的祖先，
    /// 不看函数体（体内自己的 checked/unchecked 表达式属于局部覆盖，提升后原样保留）。
    /// 嵌套时最近的一层决定种类；表达式形式（checked(expr)）只可能通过其中的
    /// lambda/匿名方法间接包含局部函数声明，同样按最近祖先判定。
    /// </summary>
    private static CheckedContextKind GetCheckedContext(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            switch (current)
            {
                case CheckedExpressionSyntax expression:
                    return expression.Keyword.IsKind(SyntaxKind.CheckedKeyword)
                        ? CheckedContextKind.Checked
                        : CheckedContextKind.Unchecked;
                case CheckedStatementSyntax statement:
                    return statement.Keyword.IsKind(SyntaxKind.CheckedKeyword)
                        ? CheckedContextKind.Checked
                        : CheckedContextKind.Unchecked;
            }

            if (current is TypeDeclarationSyntax)
                return CheckedContextKind.None;
            current = current.Parent;
        }
        return CheckedContextKind.None;
    }

    /// <summary>
    /// Returns true if the local function itself has the 'static' modifier
    /// (as opposed to merely being inside a static method).
    /// </summary>
    private static bool IsExplicitStaticLocalFunction(LocalFunctionStatementSyntax localFn)
    {
        return localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
    }

    private static bool DetermineStatic(LocalFunctionStatementSyntax localFn)
    {
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return true;

        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is LocalFunctionStatementSyntax)
            {
                current = current.Parent;
                continue;
            }
            if (current is MemberDeclarationSyntax member)
            {
                return member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            }
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// static 局部函数是否在 <c>nameof(...)</c> 里引用了外层局部/形参。static 局部
    /// 函数不能真正捕获外层局部，但 <c>nameof</c> 是编译期名字引用、合法；提升到
    /// 类级后该名字不可见，需要仍按非 static 收集捕获并折叠 <c>nameof</c>。
    /// </summary>
    private bool ReferencesEnclosingLocals(
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol localFnSymbol)
    {
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        foreach (var identifier in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;

            var symbol = _model.GetSymbolInfo(identifier).Symbol;
            // const 局部是编译期常量、不构成捕获，static 局部函数也能引用它。
            if (symbol is ILocalSymbol { HasConstantValue: true })
                continue;
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
            {
                // Skip references to the local function's own parameters
                if (SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, localFnSymbol))
                    continue;
                return true;
            }
        }
        return false;
    }

    private List<CapturePlan> CollectCaptures(
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol localFnSymbol,
        bool isStatic,
        TypeDeclarationSyntax? hostType,
        ICollection<ISymbol>? unresolvedSymbols = null)
    {
        if (isStatic)
            return new List<CapturePlan>();

        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return new List<CapturePlan>();

        var displayPosition = hostType?.OpenBraceToken.SpanStart ?? localFn.SpanStart;

        // C32 §4.2: capture discovery is the single shared primitive.
        var capParamNames = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<ISymbol>();
        var captures = CaptureClassification.CollectCapturedSymbols(
                _model,
                body,
                localFnSymbol,
                d => d is not LocalFunctionStatementSyntax,
                unresolved)
            .Select(info => CreateCapturePlan(
                info.Symbol,
                info.Name,
                LocalFunctionLiftNaming.GetCaptureParameterName(info.Name, capParamNames),
                RenderCaptureTypeDisplay(info.Type, displayPosition),
                IsCapturedVariableWritten(localFn, info.Symbol)))
            .ToList();

        // 类型符号是 ErrorType 时，退回声明处的类型语法（见 GetDeclaredTypeSyntax）。
        // 形参声明落在同一文件、同一命名空间，语法按原文件的 using 规则重新绑定，
        // 真实工程里解析出的类型与变量声明完全一致。
        foreach (var symbol in unresolved)
        {
            var typeSyntax = GetDeclaredTypeSyntax(symbol);
            if (typeSyntax == null)
            {
                unresolvedSymbols?.Add(symbol);
                continue;
            }

            var isWritten = IsCapturedVariableWritten(localFn, symbol);
            captures.Add(CreateCapturePlan(
                symbol,
                symbol.Name,
                LocalFunctionLiftNaming.GetCaptureParameterName(symbol.Name, capParamNames),
                typeSyntax,
                isWritten));
        }

        return captures;
    }

    /// <summary>
    /// 捕获的统一构造。ref 局部的形态由声明语法决定（语义类型显示是元素类型，
    /// 不能区分 `ref T` 与 `T`）：
    /// <list type="bullet">
    /// <item><c>ref T x</c> → <c>ref T</c> 形参（读、写都精确等价）；</item>
    /// <item><c>ref readonly T x</c> → <c>in T</c> 形参（只读，源本身也不能写）；</item>
    /// <item><c>scoped ref T x</c> → <c>scoped ref T</c> 形参（不逃逸契约与源一致）；</item>
    /// <item><c>scoped ref readonly T x</c> → <c>scoped in T</c> 形参（同上，只读）。</item>
    /// </list>
    /// 两处的捕获路径（语义可用 / 类型退回声明语法）必须产出同一形态。
    /// </summary>
    private static CapturePlan CreateCapturePlan(
        ISymbol symbol,
        string originalName,
        string parameterName,
        string typeDisplay,
        bool isWritten)
    {
        var refKind = GetRefLocalCaptureKind(symbol);
        return new CapturePlan(
            symbol,
            originalName,
            parameterName,
            typeDisplay,
            isWritten,
            ByRef: refKind == RefKind.Ref
                || (refKind != RefKind.RefReadOnly && isWritten),
            IsScoped: IsScopedCapture(symbol),
            ByIn: refKind == RefKind.RefReadOnly);
    }

    /// <summary>
    /// 捕获的来源声明是否带 <c>scoped</c>。优先用语义符号（
    /// <see cref="IParameterSymbol.ScopedKind"/>/<see cref="ILocalSymbol.ScopedKind"/>，
    /// 只反映显式声明，不含隐式 scoped 规则）；语义不可用时退回声明处语法
    /// （形参修饰符 <c>scoped</c>、变量声明类型 <see cref="ScopedTypeSyntax"/>、
    /// 局部声明语句的 <c>scoped</c> 修饰符）。无法判定的一律视为非 scoped，
    /// 不给普通捕获加修饰符。
    /// </summary>
    private static bool IsScopedCapture(ISymbol symbol)
    {
        switch (symbol)
        {
            case IParameterSymbol parameter:
                // out 形参在 ref-safety 规则里隐式 scoped（ScopedKind 不为 None），
                // 但那是语言内部的调用方约束；提升后按普通 ref 形参传递即可
                // （`ref p` / `scoped ref T` 两种形态都可编译），不写显式修饰符。
                if (parameter.RefKind == RefKind.Out)
                    return false;
                if (parameter.ScopedKind != ScopedKind.None)
                    return true;
                return parameter.DeclaringSyntaxReferences
                    .Select(static reference => reference.GetSyntax())
                    .OfType<ParameterSyntax>()
                    .Any(static syntax => syntax.Modifiers.Any(SyntaxKind.ScopedKeyword));

            case ILocalSymbol local:
                if (local.ScopedKind != ScopedKind.None)
                    return true;
                return local.DeclaringSyntaxReferences
                    .Select(static reference => reference.GetSyntax())
                    .OfType<VariableDeclaratorSyntax>()
                    .Any(static declarator =>
                        declarator.Parent is VariableDeclarationSyntax
                        {
                            Type: ScopedTypeSyntax,
                        }
                        || (declarator.Parent?.Parent is LocalDeclarationStatementSyntax statement
                            && statement.Modifiers.Any(SyntaxKind.ScopedKeyword)));

            default:
                return false;
        }
    }

    /// <summary>
    /// 收集局部函数（含签名里的默认值/特性实参等常量语境）引用的外层 const 局部，
    /// 并渲染成可内联的字面量文本。渲染失败时记入 <paramref name="unresolved"/>，
    /// 调用方保守跳过（不产出引用未声明名字的坏码）。
    /// </summary>
    private List<InlinedConstantPlan> CollectInlinedConstants(
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol localFnSymbol,
        TypeDeclarationSyntax? hostType,
        List<string> unresolved)
    {
        var result = new List<InlinedConstantPlan>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var displayPosition = hostType?.OpenBraceToken.SpanStart ?? localFn.SpanStart;

        // 分别扫描函数体与形参表：函数体里的引用和形参默认值/特性实参里的引用
        // 都可能落在常量语境。注意不能把局部函数声明本身当根：Roslyn 的
        // DescendantNodes(predicate) 会先用谓词判定根节点，而本谓词要排除的
        // 正是局部函数声明。
        var roots = new List<SyntaxNode>();
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body != null)
            roots.Add(body);
        if (localFn.ParameterList != null)
            roots.Add(localFn.ParameterList);

        foreach (var root in roots)
        {
            foreach (var info in CaptureClassification.CollectReferencedConstants(
                         _model,
                         root,
                         localFnSymbol,
                         static d => d is not LocalFunctionStatementSyntax))
            {
                if (!seen.Add(info.Symbol))
                    continue;

                var literal = RenderConstantLiteral(info, displayPosition);
                if (literal == null)
                {
                    unresolved.Add(info.Name);
                    continue;
                }

                result.Add(new InlinedConstantPlan(info.Symbol, info.Name, literal));
            }
        }

        return result;
    }

    /// <summary>
    /// 常量值 → C# 字面量文本。枚举常量写成 <c>(EnumType)(underlying)</c>：
    /// 常量模式要求常量能隐式转换为输入类型，裸的整数字面量对枚举不成立。
    /// </summary>
    private string? RenderConstantLiteral(
        ReferencedConstantInfo info, int displayPosition)
    {
        if (info.Type.TypeKind == TypeKind.Enum)
        {
            if (info.ConstantValue == null
                || info.Type is not INamedTypeSymbol { EnumUnderlyingType: { } underlying })
                return null;

            var inner = RenderPrimitiveLiteral(info.ConstantValue);
            return inner == null
                ? null
                : $"({RenderCaptureTypeDisplay(info.Type, displayPosition)})({inner})";
        }

        return RenderPrimitiveLiteral(info.ConstantValue);
    }

    private static string? RenderPrimitiveLiteral(object? value)
    {
        if (value is null)
            return "null";

        return value switch
        {
            bool b => b ? "true" : "false",
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            char c => SymbolDisplay.FormatLiteral(c, quote: true),
            int i => i.ToString(CultureInfo.InvariantCulture),
            uint u => u.ToString(CultureInfo.InvariantCulture) + "U",
            long l => l.ToString(CultureInfo.InvariantCulture) + "L",
            ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
            short sh => "(short)" + sh.ToString(CultureInfo.InvariantCulture),
            ushort ush => "(ushort)" + ush.ToString(CultureInfo.InvariantCulture),
            byte by => "(byte)" + by.ToString(CultureInfo.InvariantCulture),
            sbyte sb => "(sbyte)" + sb.ToString(CultureInfo.InvariantCulture),
            double d => RenderDoubleLiteral(d),
            float f => RenderFloatLiteral(f),
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "M",
            _ => null,
        };
    }

    private static string RenderDoubleLiteral(double value)
    {
        if (double.IsNaN(value))
            return "double.NaN";
        if (double.IsPositiveInfinity(value))
            return "double.PositiveInfinity";
        if (double.IsNegativeInfinity(value))
            return "double.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "D";
    }

    private static string RenderFloatLiteral(float value)
    {
        if (float.IsNaN(value))
            return "float.NaN";
        if (float.IsPositiveInfinity(value))
            return "float.PositiveInfinity";
        if (float.IsNegativeInfinity(value))
            return "float.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "F";
    }

    /// <summary>
    /// 捕获符号类型在分析编译里是 ErrorType 时的兜底类型显示：取声明处的类型语法。
    /// <list type="bullet">
    /// <item>显式类型：变量声明/形参的类型语法原样返回；</item>
    /// <item><c>var</c>：从初始化表达式提取（对象创建、cast、<c>default(T)</c>、数组创建）。</item>
    /// </list>
    /// 分析编译把整棵树的 global using 合成到一份文件里，别的工程未
    /// <c>&lt;Using Remove&gt;</c> 的命名空间会泄漏进本文件，使源码类型与元数据
    /// 类型同名二义（cs-text p05: System.Collections.Tests 的
    /// <c>var queue = new PriorityQueue&lt;int, int&gt;()</c> 同时命中源码
    /// <c>dotnet.System.Collections.Generic.PriorityQueue</c> 与 BCL
    /// <c>System.Collections.Generic.PriorityQueue</c>，CS0104）。声明处语法在
    /// 只引用本工程依赖的真实编译里是唯一可解析的，用它作形参类型不会漂移。
    /// 提取不到类型语法时返回 null，调用方保留原有的“无法解析类型”跳过。
    /// </summary>
    private static string? GetDeclaredTypeSyntax(ISymbol symbol)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            switch (reference.GetSyntax())
            {
                case ParameterSyntax parameter when parameter.Type != null:
                    return parameter.Type.WithoutTrivia().ToString();

                case VariableDeclaratorSyntax declarator
                    when declarator.Parent is VariableDeclarationSyntax declaration:
                    if (declaration.Type is not IdentifierNameSyntax { Identifier.Text: "var" })
                        return declaration.Type.WithoutTrivia().ToString();
                    return GetInitializerTypeSyntax(declarator.Initializer?.Value)?
                        .WithoutTrivia().ToString();
            }
        }

        return null;
    }

    private static TypeSyntax? GetInitializerTypeSyntax(ExpressionSyntax? expression)
        => expression switch
        {
            ObjectCreationExpressionSyntax creation => creation.Type,
            CastExpressionSyntax cast => cast.Type,
            DefaultExpressionSyntax defaultExpression => defaultExpression.Type,
            ArrayCreationExpressionSyntax array => array.Type,
            _ => null,
        };

    /// <summary>
    /// 捕获形参的类型显示。形参声明落在宿主类型的成员区，与捕获声明处同文件、
    /// 同命名空间、同类型作用域，因此优先用「该位置按语言规则可解析的最短形态」。
    ///
    /// 全树编译（ProjectPassRunner 把整棵树的源文件放进同一个 Compilation）里，
    /// 别的工程的源类型对本文件可见，<see cref="SymbolDisplayFormat.FullyQualifiedFormat"/>
    /// 会写出真实工程里并不存在的名字。cs-text p05 实例：
    /// System.Private.CoreLib 的 <c>dotnet.System.Text.StringBuilder</c> 位于本文件
    /// 命名空间 <c>dotnet.System.Text.RegularExpressions</c> 的祖先命名空间中，按简单名
    /// 查找会先命中它并遮蔽 <c>using System.Text;</c> 引入的 BCL
    /// <c>System.Text.StringBuilder</c>；生成的
    /// <c>global::dotnet.System.Text.StringBuilder</c> 形参在只引用 BCL 的 regex 工程里
    /// 是 CS0234。源声明类型退回最短形态后，名字按原代码的同一套 using/命名空间规则
    /// 重绑定，真实工程的解析结果与提升前一致。
    ///
    /// 元数据类型不受合并编译影响，仍用 <c>global::</c> 全限定形态：最短形态依赖的
    /// using 可能只来自别的工程的 global using（见 ProjectPassAdapter 的并集合成），
    /// 在真实工程里未必可见。
    /// </summary>
    private string RenderCaptureTypeDisplay(ITypeSymbol type, int position)
    {
        if (!IsDeclaredInAnalysisSource(type))
        {
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        var minimalText = type.ToMinimalDisplayString(_model, position);
        var minimalType = SyntaxFactory.ParseTypeName(minimalText);
        if (!minimalType.ContainsDiagnostics
            && BindsToTypeAtPosition(minimalType, position, type))
        {
            return minimalText;
        }

        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>
    /// 类型（含数组元素、指针目标、泛型实参）是否由本次分析编译的源文件声明。
    /// 合并编译里的源类型可能属于别的工程，真实工程编译它时不可见。
    /// </summary>
    private bool IsDeclaredInAnalysisSource(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsDeclaredInAnalysisSource(array.ElementType);
            case IPointerTypeSymbol pointer:
                return IsDeclaredInAnalysisSource(pointer.PointedAtType);
            case INamedTypeSymbol named:
                if (SymbolEqualityComparer.Default.Equals(
                        named.ContainingAssembly, _model.Compilation.Assembly))
                {
                    return true;
                }

                return named.TypeArguments.Any(IsDeclaredInAnalysisSource);
            default:
                return false;
        }
    }

    private bool BindsToTypeAtPosition(
        TypeSyntax typeSyntax, int position, ITypeSymbol expected)
    {
        try
        {
            return _model.GetSpeculativeSymbolInfo(
                    position, typeSyntax, SpeculativeBindingOption.BindAsTypeOrNamespace)
                .Symbol is ITypeSymbol resolved
                && SymbolEqualityComparer.Default.Equals(resolved, expected);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool IsCapturedVariableWritten(
        LocalFunctionStatementSyntax localFn,
        ISymbol symbol)
    {
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        // C32 §4.2: write-site discovery is the single shared primitive.
        return CaptureClassification.FindWriteSites(
                _model,
                symbol,
                body,
                d => d is not LocalFunctionStatementSyntax)
            .Count > 0;
    }

    /// <summary>
    /// 枚举 <paramref name="body"/> 里可能引用局部函数的名字节点：
    /// <see cref="IdentifierNameSyntax"/> 与显式类型实参形式
    /// <see cref="GenericNameSyntax"/>（<c>Other&lt;int&gt;</c> 方法组引用）。
    /// 排除命名实参名、<c>nameof(...)</c> 内部与成员访问的 Name 部分
    /// （<c>obj.Other</c> 不是本作用域的局部函数）。调用目标（含泛型调用）
    /// 也在结果里，由调用方按需区分「调用点」与「方法组引用」——两者都需要
    /// 传播捕获，但只有方法组引用必然落进重写器合成的 lambda。
    /// 与捕获收集一致，不下降进更深层的局部函数声明（它们各自有独立 LiftPlan）。
    /// </summary>
    private static IEnumerable<SimpleNameSyntax> EnumerateLocalFunctionReferences(SyntaxNode body)
    {
        foreach (var name in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<SimpleNameSyntax>())
        {
            if (name is not (IdentifierNameSyntax or GenericNameSyntax))
                continue;
            if (name.Parent is NameColonSyntax)
                continue;
            if (CaptureClassification.IsInsideNameOf(name))
                continue;
            if (name.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name == name)
                continue;

            yield return name;
        }
    }

    private void PropagateCaptures(List<LiftPlan> plans)
    {
        var bySymbol = plans.Where(p => p.Symbol != null)
            .ToDictionary(p => p.Symbol!, p => p, SymbolEqualityComparer.Default);
        var activePlans = plans.Where(p => !p.Skipped).ToList();

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var plan in plans)
            {
                if (plan.Skipped)
                    continue;

                var body = (SyntaxNode?)plan.Node.Body ?? plan.Node.ExpressionBody;
                if (body == null)
                    continue;

                foreach (var reference in EnumerateLocalFunctionReferences(body))
                {
                    // 绑定失败的引用也要按词法作用域归属到 callee，否则 callee 的
                    // 捕获不会传播到 caller，提升后 caller 方法体里出现未声明名字
                    // （CS0103）。
                    var calleePlan = LocalFunctionCallSiteResolver.Resolve(
                        reference, reference.Identifier.Text, activePlans, _model, bySymbol);
                    if (calleePlan == null || calleePlan.Skipped)
                        continue;

                    foreach (var capture in calleePlan.Captures)
                    {
                        // 嵌套局部函数捕获的变量可能就是 caller 自己的局部变量
                        // （例如 Compute 里的 charSetSolver 被它内部的
                        // MapCategoryCodeSetToCondition 捕获）。这不是 caller 的
                        // 外部捕获：caller 提升后，callee 调用点仍在 caller 方法体
                        // 内，直接引用该局部变量即可。若把它传播成 caller 的形参，
                        // caller 的外部调用点会被追加一个根本不在作用域内的实参
                        // （CS0103）。
                        if (CaptureClassification.IsDeclaredInside(capture.Symbol, body))
                            continue;

                        // caller 自己的形参同理：嵌套局部函数捕获它时，提升后的
                        // caller 方法里它就是普通形参，直接引用即可。若传播成
                        // caller 的捕获，会产生与形参同名的重复形参（CS0100），
                        // 或按名字把形参引用错误改写成捕获参数
                        // （gen/RegexGenerator.Emitter.cs 的 EmitAlternation）。
                        if (SymbolEqualityComparer.Default.Equals(
                                capture.Symbol.ContainingSymbol, plan.Symbol))
                            continue;

                        var existing = plan.Captures.FirstOrDefault(c =>
                            SymbolEqualityComparer.Default.Equals(c.Symbol, capture.Symbol));
                        if (existing == null)
                        {
                            // 两个不同符号可能同名（例如 caller 自有的 node 与 callee 参数 node），
                            // 各自独立分配后都得到 _cs2j_cap_node。提升后的形参名必须在
                            // caller 的提升方法作用域内唯一，否则出现重复形参（CS0100）。
                            plan.Captures.Add(capture with
                            {
                                ParameterName = AllocateCaptureParameterName(plan, capture.OriginalName),
                            });
                            changed = true;
                        }
                        else if (capture.IsWritten && !existing.IsWritten)
                        {
                            plan.Captures[plan.Captures.IndexOf(existing)] =
                                existing with { IsWritten = true, ByRef = true };
                            changed = true;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 在 caller 提升后的方法作用域内为新捕获分配唯一形参名：
    /// 避开已有捕获形参、原方法参数名与类型参数名。
    /// </summary>
    private static string AllocateCaptureParameterName(LiftPlan plan, string originalName)
    {
        var usedNames = new HashSet<string>(
            plan.Captures.Select(c => c.ParameterName), StringComparer.Ordinal);
        if (plan.Node.ParameterList != null)
        {
            foreach (var parameter in plan.Node.ParameterList.Parameters)
                usedNames.Add(parameter.Identifier.Text);
        }
        if (plan.Node.TypeParameterList != null)
        {
            foreach (var typeParameter in plan.Node.TypeParameterList.Parameters)
                usedNames.Add(typeParameter.Identifier.Text);
        }
        return LocalFunctionLiftNaming.GetCaptureParameterName(originalName, usedNames);
    }

    private void CheckPostPropagationGates(
        LiftPlan plan,
        IReadOnlyDictionary<ISymbol, LiftPlan> bySymbol,
        IReadOnlyList<LiftPlan> activePlans)
    {
        // 写入型捕获提升为 ref 形参后，调用点的实参必须确定赋值（CS0165）。
        // 原局部函数允许「局部声明时不初始化、只在其函数体内赋值」
        // （cs-text p05: RegexCharClass.renderRanges 的 ch1/ch2）。这类声明在重写器
        // 里补 `= default(T)`：原代码能通过确定赋值分析，说明读取前必经赋值，
        // 补默认值只影响确定赋值，不改变运行语义。只有声明形式无法原地补
        // 初始化器（using/fixed/ref 局部等）时才保留原局部函数。
        // holder 捕获按值传入 StrongBox，不存在确定赋值要求，不触发该闸门。
        var unassignedCapture = plan.Captures.FirstOrDefault(c =>
            c.IsWritten && !c.UseHolder && c.Symbol is ILocalSymbol local
            && !HasInitializer(c.Symbol) && !CanSeedDefaultInitializer(local));
        if (unassignedCapture != null)
        {
            var local = (ILocalSymbol)unassignedCapture.Symbol;
            Skip(plan, LocalFunctionSkipReason.RefCaptureNotDefinitelyAssigned,
                $"write capture '{unassignedCapture.OriginalName}' is declared without an " +
                "initializer and cannot be seeded with a default value " +
                $"({GetSeedingUnavailableReason(local)}); the lifted ref parameter would require " +
                "definite assignment at every call site (CS0165)");
            return;
        }

        foreach (var cap in plan.Captures)
        {
            // ref 局部捕获无论是否被写都必须走 ref/in 形参（读取也要保持引用语义）。
            if (cap.UseHolder || (!cap.IsWritten && !cap.ByRef && !cap.ByIn))
                continue;

            if (!CanUseRefCapture(plan, cap))
            {
                Skip(plan, LocalFunctionSkipReason.RefCaptureUnavailable,
                    $"capture '{cap.OriginalName}' cannot use a ref/in parameter: " +
                    GetRefCaptureUnavailableReason(plan, cap));
                return;
            }
        }

        // 写入型捕获若必须以 ref 形参传递，就不能出现在 lambda 内（CS1628）。
        // 这类捕获已在上一步尽量改用 StrongBox holder；只有 holder 不可表达
        // （如 ref struct / 指针 / 参数捕获等）时才保留原局部函数。
        var offending = FindLambdaExposedWrittenCaptures(plan, bySymbol, activePlans)
            .FirstOrDefault(static c => !c.UseHolder);
        if (offending != null)
        {
            var lineSpan = plan.Node.GetLocation().GetLineSpan();
            var line = lineSpan.IsValid ? lineSpan.StartLinePosition.Line + 1 : 0;
            var detail = IsRefLocalCapture(offending.Symbol)
                ? "the capture is a ref local and its reference lands in a lambda; a ref/in " +
                  "parameter cannot be used there (CS1628)"
                : "a ref parameter cannot be used there (CS1628) and the capture type cannot be " +
                  $"wrapped in a StrongBox holder ({GetHolderUnavailableReason(offending)})";
            Skip(plan, LocalFunctionSkipReason.RefCaptureInNestedLambda,
                $"capture '{offending.OriginalName}' would be referenced from a lambda in the " +
                $"hoisted body (local function starts at line {line}); {detail}");
        }

        // ref 局部捕获的调用点实参必须是 `ref rx` / `in rx`，该形态在 lambda /
        // 匿名方法 / 查询表达式内非法（CS8175），只能保留原局部函数。
        var refLocalCapture = plan.Captures.FirstOrDefault(static c =>
            !c.UseHolder && IsRefLocalCapture(c.Symbol));
        if (refLocalCapture != null
            && HasRefLocalCaptureArgumentInsideLambda(
                plan, refLocalCapture.Symbol, activePlans, bySymbol))
        {
            Skip(plan, LocalFunctionSkipReason.RefCaptureInNestedLambda,
                $"ref local capture '{refLocalCapture.OriginalName}' is used by a call site " +
                "inside a lambda/anonymous method/query expression; 'ref rx' is not allowed " +
                "there (CS8175)");
        }
    }

    /// <summary>
    /// 提升后的调用点是否会落在 lambda / 匿名方法 / 查询表达式里：这种位置的实参
    /// 必须是 <c>ref rx</c> / <c>in rx</c>（ref 局部），源里非法（CS8175）；方法组
    /// 引用会被重写器展开成带 ref 实参的合成 lambda，同样算命中。ref 局部本身与
    /// 引用位于同一个 lambda 内时合法（那时它是该 lambda 的局部变量）。
    /// </summary>
    private bool HasRefLocalCaptureArgumentInsideLambda(
        LiftPlan plan,
        ISymbol symbol,
        IReadOnlyList<LiftPlan> activePlans,
        IReadOnlyDictionary<ISymbol, LiftPlan> bySymbol)
    {
        var scope = LocalFunctionCallSiteResolver.GetStatementContainer(plan.Node);
        if (scope == null)
            return false;

        foreach (var reference in EnumerateLocalFunctionReferences(scope))
        {
            // 绑定失败的引用按词法作用域归属；归属不到 plan 的引用不属于本调用点。
            var referenced = LocalFunctionCallSiteResolver.Resolve(
                reference, reference.Identifier.Text, activePlans, _model, bySymbol);
            if (!ReferenceEquals(referenced, plan))
                continue;

            var isInvocationTarget = reference.Parent is InvocationExpressionSyntax invocation
                && invocation.Expression == reference;

            for (var ancestor = reference.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor is not (AnonymousFunctionExpressionSyntax or QueryExpressionSyntax))
                    continue;

                if (CaptureClassification.IsDeclaredInside(symbol, ancestor))
                    break; // ref 局部与该引用同处这个 lambda 内：ref 实参合法
                return true;
            }

            // 方法组引用会变成 `(args) => callee(ref rx, args)`，ref 实参必然落进 lambda。
            if (!isInvocationTarget)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 收集「提升后会落进 lambda、因而不能是 ref 形参」的写入型捕获，返回本 plan
    /// 自己的 CapturePlan 实例（决定 holder 标记用）：
    /// - 方法组引用（任何位置）：重写器会合成 `(args) =&gt; callee(caps, args)`，
    ///   该 lambda 会以 ref 形式携带 callee 的全部写入型捕获；
    /// - 直接调用，且调用点位于已有 lambda / 匿名方法内：重写器插入的 `ref cap`
    ///   实参落在 lambda 内；
    /// - lambda 内直接引用写入型捕获（读或写）。
    /// 与捕获收集一致，不下降进更深层的局部函数声明（它们各自有独立的 LiftPlan）。
    /// </summary>
    private List<CapturePlan> FindLambdaExposedWrittenCaptures(
        LiftPlan plan,
        IReadOnlyDictionary<ISymbol, LiftPlan> bySymbol,
        IReadOnlyList<LiftPlan> activePlans)
    {
        var result = new List<CapturePlan>();
        var body = (SyntaxNode?)plan.Node.Body ?? plan.Node.ExpressionBody;
        if (body == null)
            return result;

        // 提升后必须以 ref/in 形参携带的捕获：普通写入型捕获与 ref 局部捕获
        // （ref 局部即使只读也要以 ref/in 传，读取才保持引用语义）。这些形参
        // 都不能出现在 lambda 内（CS1628），实参也不能在 lambda 里传 ref（CS8175）。
        var writtenCaptures = plan.Captures
            .Where(static c => c.IsWritten || IsRefLocalCapture(c.Symbol))
            .ToList();
        if (writtenCaptures.Count == 0)
            return result;

        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var reference in EnumerateLocalFunctionReferences(body))
        {
            var insideLambda = IsInsideAnonymousFunction(reference, body);
            var symbol = _model.GetSymbolInfo(reference).Symbol;

            // 绑定失败的同名引用也按词法作用域归属到 callee，否则「未绑定 callee
            // 的写入型捕获经方法组 / lambda 暴露」会漏检，产出 CS1628/CS8175。
            var calleePlan = LocalFunctionCallSiteResolver.Resolve(
                reference, reference.Identifier.Text, activePlans, _model, bySymbol);
            if (calleePlan != null && !calleePlan.Skipped)
            {
                var calleeWritten = calleePlan.Captures
                    .Where(static c => c.IsWritten || IsRefLocalCapture(c.Symbol))
                    .ToList();
                if (calleeWritten.Count == 0)
                    continue;

                var isInvocationTarget = reference.Parent is InvocationExpressionSyntax invocation
                    && invocation.Expression == reference;
                if (isInvocationTarget && !insideLambda)
                    continue;

                // 只报告本 plan 确实以 ref 形参携带的捕获（捕获传播已合并 callee 的捕获）。
                foreach (var calleeCapture in calleeWritten)
                {
                    var propagated = writtenCaptures.FirstOrDefault(c =>
                        SymbolEqualityComparer.Default.Equals(c.Symbol, calleeCapture.Symbol));
                    if (propagated != null && seen.Add(propagated.Symbol))
                        result.Add(propagated);
                }
                continue;
            }

            if (!insideLambda)
                continue;

            var directCapture = writtenCaptures.FirstOrDefault(c =>
                SymbolEqualityComparer.Default.Equals(c.Symbol, symbol));
            if (directCapture != null && seen.Add(directCapture.Symbol))
                result.Add(directCapture);
        }
        return result;
    }

    private HashSet<ISymbol> CollectHolderCaptureSymbols(
        List<LiftPlan> plans, IReadOnlyDictionary<ISymbol, LiftPlan> bySymbol)
    {
        var activePlans = plans.Where(p => !p.Skipped).ToList();
        var holders = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var plan in plans)
        {
            if (plan.Skipped)
                continue;

            // 迭代器局部函数不能有 ref/in/out 形参（CS1623），async 局部函数也是
            // 同理（异步方法不允许 ref 形参）：写入型捕获必须以 StrongBox 值传递。
            // 无法 holder 的捕获留给 CheckPostPropagationGates 按 ref 不可用跳过。
            if (RequiresValueCapture(plan))
            {
                foreach (var capture in plan.Captures)
                {
                    if (capture.IsWritten && CanUseHolderCapture(capture))
                        holders.Add(capture.Symbol);
                }
            }

            foreach (var capture in FindLambdaExposedWrittenCaptures(plan, bySymbol, activePlans))
            {
                if (CanUseHolderCapture(capture))
                    holders.Add(capture.Symbol);
            }
        }
        return holders;
    }

    /// <summary>
    /// 把 holder 决策应用到所有 plan 的同符号捕获。变量本身变成 StrongBox 后，
    /// 即使某个提升函数只是读取它，也必须接收 StrongBox 形参。
    /// </summary>
    private void ApplyHolderCaptures(List<LiftPlan> plans, HashSet<ISymbol> holderSymbols)
    {
        if (holderSymbols.Count == 0)
            return;

        foreach (var plan in plans)
        {
            for (var i = 0; i < plan.Captures.Count; i++)
            {
                var capture = plan.Captures[i];
                if (capture.UseHolder || !holderSymbols.Contains(capture.Symbol))
                    continue;

                var type = GetCaptureType(capture.Symbol);
                if (type == null)
                    continue;

                plan.Captures[i] = capture with
                {
                    UseHolder = true,
                    ByRef = false,
                    HolderElementTypeDisplay = RenderCaptureTypeDisplay(
                        type,
                        plan.HostType?.OpenBraceToken.SpanStart ?? plan.Node.SpanStart),
                };
            }
        }
    }

    private static ITypeSymbol? GetCaptureType(ISymbol symbol)
        => symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null,
        };

    /// <summary>
    /// 该捕获能否安全改写为 StrongBox 闭包：
    /// - 必须是有普通局部声明语句覆盖的局部变量（参数/foreach/using/fixed/const 无法原地改写）；
    /// - 类型必须能作泛型实参（非 ref struct、非指针、非函数指针、非未绑定泛型）。
    /// </summary>
    private bool CanUseHolderCapture(CapturePlan capture)
    {
        // scoped 捕获改成 StrongBox 会丢失 scoped 约束，或让声明处的 scoped
        // 变量被包装成可逃逸的引用；保守跳过，交给调用方按 ref 不可用诊断。
        if (capture.IsScoped)
            return false;

        if (capture.Symbol is not ILocalSymbol local || local.IsConst)
            return false;

        if (!IsValidStrongBoxElementType(local.Type))
            return false;

        var declarator = local.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault();
        if (declarator?.Parent is not VariableDeclarationSyntax declaration)
            return false;
        if (declaration.Variables.Count != 1)
            return false;
        if (declaration.Parent is not LocalDeclarationStatementSyntax statement)
            return false;
        if (!statement.UsingKeyword.IsKind(SyntaxKind.None)
            || !statement.AwaitKeyword.IsKind(SyntaxKind.None))
            return false;
        if (statement.Modifiers.Any(static m => m.IsKind(SyntaxKind.ConstKeyword)))
            return false;
        if (declaration.Type is RefTypeSyntax or ScopedTypeSyntax)
            return false;

        return true;
    }

    private static string GetHolderUnavailableReason(CapturePlan capture)
    {
        if (capture.IsScoped)
            return "the capture is declared 'scoped' and cannot be wrapped in a StrongBox";
        if (capture.Symbol is not ILocalSymbol local)
            return $"capture is a {capture.Symbol.Kind}, not a plain local variable";
        if (local.IsConst)
            return "capture is a const local";
        if (!IsValidStrongBoxElementType(local.Type))
            return $"type '{local.Type.ToDisplayString()}' cannot be a StrongBox type argument";
        return "the local declaration cannot be rewritten in place (foreach/using/fixed/multi-declarator)";
    }

    private static bool IsValidStrongBoxElementType(ITypeSymbol type)
    {
        switch (type)
        {
            case IPointerTypeSymbol:
            case IFunctionPointerTypeSymbol:
                return false;
            case IArrayTypeSymbol array:
                return IsValidStrongBoxElementType(array.ElementType);
        }

        if (type.IsRefLikeType)
            return false;
        if (type.TypeKind == TypeKind.Error)
            return false;

        if (type is INamedTypeSymbol named)
        {
            if (named.IsUnboundGenericType)
                return false;
            foreach (var argument in named.TypeArguments)
            {
                if (!IsValidStrongBoxElementType(argument))
                    return false;
            }
        }

        return true;
    }

    private static bool IsInsideAnonymousFunction(SyntaxNode node, SyntaxNode scope)
    {
        for (var current = node.Parent; current != null && current != scope; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Gate #12: If a non-skipped local function references a skipped local
    /// function, it must also be skipped because the skipped function remains
    /// local and cannot be accessed from the class-level method.
    /// Uses iterative fixpoint propagation because skipping a function may
    /// cause other functions that reference it to also need skipping.
    /// </summary>
    private void CheckReferencesToSkippedLocalFunctions(List<LiftPlan> plans)
    {
        var skippedSymbols = new HashSet<ISymbol>(
            plans.Where(p => p.Skipped && p.Symbol != null)
                 .Select(p => p.Symbol!),
            SymbolEqualityComparer.Default);
        var bySymbol = plans
            .Where(p => p.Symbol != null)
            .ToDictionary(p => p.Symbol!, p => p, SymbolEqualityComparer.Default);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var plan in plans)
            {
                if (plan.Skipped)
                    continue;

                var body = (SyntaxNode?)plan.Node.Body ?? plan.Node.ExpressionBody;
                if (body == null)
                    continue;

                foreach (var reference in EnumerateLocalFunctionReferences(body))
                {
                    var symbol = _model.GetSymbolInfo(reference).Symbol;
                    var referencesSkipped = symbol != null
                        ? skippedSymbols.Contains(symbol)
                          || (symbol is IMethodSymbol method
                              && skippedSymbols.Contains(method.OriginalDefinition))
                        // 绑定失败的引用按词法作用域归属（含已跳过 plan），
                        // 否则提升后的 caller 仍调用一个保留下来的局部函数 → CS0103。
                        : LocalFunctionCallSiteResolver.Resolve(
                              reference,
                              reference.Identifier.Text,
                              plans,
                              _model,
                              bySymbol,
                              includeSkipped: true) is { Skipped: true };

                    if (referencesSkipped)
                    {
                        Skip(plan, LocalFunctionSkipReason.ReferencesSkippedLocalFunction,
                            $"references skipped local function '{reference.Identifier.Text}'");
                        if (plan.Symbol != null)
                            skippedSymbols.Add(plan.Symbol);
                        changed = true;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 提升后的方法不能有 ref 形参：async 方法（CS1988）与迭代器方法（CS1623）。
    /// 这类函数的所有写入型捕获都必须改走 StrongBox holder。
    /// </summary>
    private static bool RequiresValueCapture(LiftPlan plan)
        => plan.Node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword))
           || ContainsYield(plan.Node);

    /// <summary>
    /// 函数体是否含 yield（迭代器）。不下降进嵌套局部函数（它们有独立 plan），
    /// lambda 里的 yield 本身就是非法语法，无需特殊处理。
    /// </summary>
    private static bool ContainsYield(LocalFunctionStatementSyntax node)
    {
        var body = (SyntaxNode?)node.Body ?? node.ExpressionBody;
        return body != null
            && body.DescendantNodes(d => d is not LocalFunctionStatementSyntax)
                .OfType<YieldStatementSyntax>()
                .Any();
    }

    private static bool CanUseRefCapture(LiftPlan plan, CapturePlan cap)
    {
        // scoped 的 *ref 局部* 捕获可以精确保留契约：形参写成 `scoped ref T` /
        // `scoped in T`，调用点的 `ref rx` / `in rx` 合法（scoped 形参承诺不逃逸，
        // 与源变量的 scoped 声明一致）。其它 scoped 捕获（值变量/形参）没有等价
        // 形态：形参一旦是 ref 就得丢掉 scoped → 调用点把 scoped 变量传给非 scoped
        // 形参（CS8352）；保留 scoped 又无法回写。这类捕获保守跳过。
        if (cap.IsScoped && !IsRefLocalCapture(cap.Symbol))
            return false;

        // async/迭代器提升方法的写入型捕获优先改走 StrongBox holder；只有 holder
        // 不可表达（ref struct/指针/参数捕获等）时才会落到这里按 ref 不可用跳过。
        if (RequiresValueCapture(plan))
            return false;

        var sym = cap.Symbol;
        if (sym is ILocalSymbol)
        {
            var decl = FindVariableDeclarator(plan.Node, sym);
            if (decl != null)
            {
                if (IsForeachOrUsingOrFixedVariable(decl))
                    return false;
                if (decl.Initializer == null)
                    return false;
            }
        }
        if (sym is IParameterSymbol param)
        {
            if (param.RefKind is RefKind.In)
                return false;
            // out 形参可以走 `ref p`：重写器在类级方法/构造函数体开头补
            // `p = default(T);`，保证所有调用点的 ref 实参满足确定赋值。
            if (param.RefKind is RefKind.Out && !CanSeedOutParameter(param))
                return false;
        }

        return true;
    }

    /// <summary>
    /// out 形参能否以 <c>ref p</c> 实参传入。源里 out 形参只在方法体内按路径赋值，
    /// <c>ref</c> 实参却要求调用点明确赋值：提升时在包含它的类级方法/构造函数
    /// 体开头插一条 <c>p = default(T);</c>（每个参数最多一条）即可。out 形参必须
    /// 在正常返回前赋值、异常路径上调用方拿不到值，因此补默认值不改变可观察语义。
    /// 只有类级方法/构造函数有可插入的语句体；局部函数的 out 形参（嵌套提升）
    /// 本轮不处理。
    /// </summary>
    private static bool CanSeedOutParameter(IParameterSymbol parameter)
        => parameter.ContainingSymbol is IMethodSymbol
        {
            MethodKind: MethodKind.Ordinary or MethodKind.Constructor,
        };

    private static string GetRefCaptureUnavailableReason(LiftPlan plan, CapturePlan cap)
    {
        if (cap.IsScoped)
            return $"capture '{cap.OriginalName}' is declared 'scoped' but is not a ref local; " +
                "a ref capture parameter would drop the scoped contract (the call site would be " +
                "CS8352) and a by-value parameter cannot write back";
        // 以下 async/迭代器原因仅在该捕获无法改写为 StrongBox holder 时才会产生
        // （可 holder 的写入型捕获已由 CollectHolderCaptureSymbols 提前接管）。
        if (plan.Node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            return "local function is async (async methods cannot have ref/in/out parameters, CS1988)";
        if (ContainsYield(plan.Node))
            return "local function is an iterator (yield): iterator methods cannot have ref/in/out parameters (CS1623)";
        if (cap.Symbol is ILocalSymbol)
        {
            var decl = FindVariableDeclarator(plan.Node, cap.Symbol);
            if (decl != null)
            {
                if (IsForeachOrUsingOrFixedVariable(decl))
                    return $"variable '{cap.OriginalName}' is a foreach/using/fixed variable";
                if (decl.Initializer == null)
                    return $"variable '{cap.OriginalName}' has no initializer";
            }
        }
        if (cap.Symbol is IParameterSymbol param)
        {
            if (param.RefKind is RefKind.In)
                return $"parameter '{cap.OriginalName}' is 'in'";
            if (param.RefKind is RefKind.Out)
                return param.ContainingSymbol is IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary or MethodKind.Constructor,
                }
                    ? $"parameter '{cap.OriginalName}' is 'out'"
                    : $"parameter '{cap.OriginalName}' is 'out' of a local function; only " +
                        "out parameters of class-level methods/constructors can be seeded";
        }
        return "ref not available";
    }

    /// <summary>
    /// 被捕获的局部变量声明时是否带初始化器。带初始化器才能保证调用点确定赋值
    /// （ref 实参的前提）；模式变量/解构声明本身就是赋值，不算未赋值。
    /// </summary>
    private static bool HasInitializer(ISymbol symbol)
    {
        var declaring = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        return declaring is not VariableDeclaratorSyntax declarator
            || declarator.Initializer != null;
    }

    /// <summary>
    /// 无初始化器的局部能否在声明处补 `= default(T)` 以满足调用点的确定赋值：
    /// 只有普通变量声明（局部声明语句 / for 初始化声明，非 var、非 ref/using/
    /// fixed/const）可以原地补初始化器。
    /// </summary>
    private static bool CanSeedDefaultInitializer(ILocalSymbol local)
        => GetSeedingUnavailableReason(local) == null;

    private static string? GetSeedingUnavailableReason(ILocalSymbol local)
    {
        var declarator = local.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault();
        if (declarator?.Parent is not VariableDeclarationSyntax declaration)
            return "the local is declared by a pattern, deconstruction or foreach";
        if (declaration.Type is RefTypeSyntax)
            return "'ref' local";
        if (declaration.Type is IdentifierNameSyntax { Identifier.Text: "var" })
            return "'var' local without initializer";
        if (declaration.Parent is LocalDeclarationStatementSyntax statement
            && (!statement.UsingKeyword.IsKind(SyntaxKind.None)
                || !statement.AwaitKeyword.IsKind(SyntaxKind.None)
                || statement.Modifiers.Any(static m => m.IsKind(SyntaxKind.ConstKeyword))))
            return "declared in a using/const local statement";
        if (declaration.Parent is not LocalDeclarationStatementSyntax and not ForStatementSyntax)
            return "declared in a fixed/using statement";
        return null;
    }

    /// <summary>
    /// 收集条件编译区里以函数式引用（<c>name(...)</c> / <c>nameof(name)</c>）出现的
    /// 标识符及其位置。禁用区是 DisabledTextTrivia（不透明文本），重写器无法重命名
    /// 其中的引用；这些区域在真实工程里可能被编译（如 generator 工程定义了 DEBUG）。
    /// 只收函数式引用：同名局部变量/字段的普通出现不影响提升。
    /// </summary>
    private void CollectNamesReferencedInDisabledText(CompilationUnitSyntax root)
    {
        foreach (var token in root.DescendantTokens())
        {
            CollectFromDisabledText(token.LeadingTrivia, token);
            CollectFromDisabledText(token.TrailingTrivia, token);
        }
    }

    private void CollectFromDisabledText(SyntaxTriviaList triviaList, SyntaxToken owner)
    {
        foreach (var trivia in triviaList)
        {
            if (!trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                continue;

            var text = trivia.ToString();
            _disabledTextTexts.Add(text);

            // 禁用区所属的成员（方法/属性/字段等）：改名时用来确认引用确实落在
            // 声明该局部函数的同一成员内。
            var memberSpan = owner.Parent?
                .AncestorsAndSelf()
                .OfType<MemberDeclarationSyntax>()
                .FirstOrDefault()?.Span ?? default;

            var index = 0;
            while (index < text.Length)
            {
                if (!SyntaxFacts.IsIdentifierStartCharacter(text[index]))
                {
                    index++;
                    continue;
                }

                var start = index;
                while (index < text.Length && SyntaxFacts.IsIdentifierPartCharacter(text[index]))
                    index++;

                var name = text.Substring(start, index - start);
                if (!IsFunctionStyleReference(text, start, index))
                    continue;

                if (!_disabledTextReferences.TryGetValue(name, out var references))
                {
                    references = new List<DisabledTextReference>();
                    _disabledTextReferences[name] = references;
                }

                references.Add(new DisabledTextReference(
                    trivia.SpanStart, start, index - start, memberSpan));
            }
        }
    }

    private static bool IsFunctionStyleReference(string text, int start, int end)
    {
        // nameof(Name)：前一个非空白记号是 nameof(。
        var head = text.AsSpan(0, start).TrimEnd();
        if (!head.IsEmpty && head[^1] == '(')
        {
            var callee = head[..^1].TrimEnd();
            if (callee.EndsWith("nameof".AsSpan(), StringComparison.Ordinal))
            {
                var prefixLength = callee.Length - "nameof".Length;
                if (prefixLength == 0
                    || !SyntaxFacts.IsIdentifierPartCharacter(callee[prefixLength - 1]))
                {
                    return true;
                }
            }
        }

        // Name(...)：后一个非空白字符是 '('。
        var tail = text.AsSpan(end);
        var next = 0;
        while (next < tail.Length && char.IsWhiteSpace(tail[next]))
            next++;
        return next < tail.Length && tail[next] == '(';
    }

    private static VariableDeclaratorSyntax? FindVariableDeclarator(
        SyntaxNode scope, ISymbol symbol)
    {
        foreach (var decl in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (decl.Identifier.Text == symbol.Name)
                return decl;
        }
        return null;
    }

    private static bool IsForeachOrUsingOrFixedVariable(VariableDeclaratorSyntax decl)
    {
        SyntaxNode? current = decl.Parent;
        while (current != null)
        {
            if (current is ForEachStatementSyntax
                or UsingStatementSyntax
                or FixedStatementSyntax)
                return true;
            if (current is MemberDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// 本 pass 的捕获类型闸门。局部函数可以捕获指针/函数指针（声明处已在 unsafe
    /// 上下文内），提升后的方法写成 <c>private unsafe</c> 并把指针作为形参完全合法；
    /// 重写器按签名里的 <see cref="PointerTypeSyntax"/>/<see cref="FunctionPointerTypeSyntax"/>
    /// 自动补 <c>unsafe</c>。因此这里放行可表达的指针形态：指针、指向指针的指针、
    /// 以指针为元素的数组、函数指针。
    /// <para>
    /// 共享原语 <see cref="CaptureClassification.IsUnsupportedCaptureKind"/> 对
    /// '<c>*</c>' 一律拒绝（closure-lift 的 lambda 确实不能捕获指针，C32 依赖该
    /// 语义），所以不能全局放宽；匿名类型（不可拼写的 <c>&lt;anonymous&gt;</c> 名）
    /// 与 ref 前缀类型仍按共享原语跳过。
    /// </para>
    /// </summary>
    private static bool IsUnsupportedCaptureType(CapturePlan cap)
    {
        var type = GetCaptureType(cap.Symbol);
        if (type != null && IsExpressiblePointerCapture(type))
            return false;

        // ref 局部捕获：`ref T` → `ref T` 形参、`ref readonly T` → `in T` 形参，
        // `scoped ref T` → `scoped ref T`、`scoped ref readonly T` → `scoped in T`
        // 形参，与源语义/不逃逸契约等价（见 CanUseRefCapture）。只剩元素类型本身
        // 不可拼写（匿名类型等）按不支持跳过。
        if (IsRefLocalCapture(cap.Symbol))
            return CaptureClassification.IsUnsupportedCaptureKind(cap.TypeDisplay);

        return CaptureClassification.IsUnsupportedCaptureKind(cap.TypeDisplay);
    }

    private static bool IsExpressiblePointerCapture(ITypeSymbol type)
        => type switch
        {
            IPointerTypeSymbol => true,          // int* / void* / int**
            IFunctionPointerTypeSymbol => true,  // delegate* unmanaged<int, void>
            IArrayTypeSymbol array => IsExpressiblePointerCapture(array.ElementType),
            _ => false,
        };

    private static bool IsRefLocalCapture(ISymbol symbol)
        => GetRefLocalCaptureKind(symbol) != null;

    /// <summary>
    /// ref 局部的引用形态（<c>ref T</c> → <see cref="RefKind.Ref"/>，
    /// <c>ref readonly T</c> → <see cref="RefKind.RefReadOnly"/>），非 ref 局部返回
    /// <see langword="null"/>。语义符号的类型显示是元素类型（<c>ref int</c> 显示为
    /// <c>int</c>），必须按声明语法判定；<c>scoped</c> 前缀不影响形态判定。
    /// </summary>
    private static RefKind? GetRefLocalCaptureKind(ISymbol symbol)
    {
        if (symbol is not ILocalSymbol local)
            return null;

        foreach (var reference in local.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not VariableDeclaratorSyntax
                { Parent: VariableDeclarationSyntax declaration })
                continue;

            var type = declaration.Type;
            if (type is ScopedTypeSyntax scoped)
                type = scoped.Type;
            if (type is RefTypeSyntax refType)
            {
                return refType.ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword)
                    ? RefKind.RefReadOnly
                    : RefKind.Ref;
            }
        }

        return null;
    }

    private static bool HasParamsOrOptional(LocalFunctionStatementSyntax localFn)
    {
        if (localFn.ParameterList == null)
            return false;
        foreach (var param in localFn.ParameterList.Parameters)
        {
            if (param.Default != null)
                return true;
            if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
                return true;
        }
        return false;
    }

    private string? AssignName(
        LiftPlan plan,
        LocalFunctionStatementSyntax localFn,
        TypeDeclarationSyntax? hostType,
        IMethodSymbol symbol)
    {
        var baseName = LocalFunctionLiftNaming.GetBaseName(localFn.Identifier.Text);

        var typeSymbol = hostType != null
            ? _model.GetDeclaredSymbol(hostType) as INamedTypeSymbol
            : null;
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        if (typeSymbol != null)
        {
            foreach (var member in typeSymbol.GetMembers())
                memberNames.Add(member.Name);
        }

        // 顶层宿主没有语法类型：用合成类名做命名键，所有顶层局部函数共用一组
        // 已用名字，并避开与类名同名（同名方法会变成构造函数，CS0542）。
        var hostName = hostType?.Identifier.Text ?? plan.TopLevelHostClassName ?? "";
        var typeKey = typeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? (plan.IsTopLevelHost ? "top-level:" + hostName : hostName);
        if (!_crossFileNameTable.TryGetValue(typeKey, out var usedNames))
        {
            usedNames = new HashSet<string>(StringComparer.Ordinal);
            _crossFileNameTable[typeKey] = usedNames;
        }

        var combined = new HashSet<string>(memberNames, StringComparer.Ordinal);
        foreach (var n in usedNames)
            combined.Add(n);
        if (plan.IsTopLevelHost)
            combined.Add(hostName);

        // Collect local variable names in the enclosing method to avoid
        // naming conflicts when the hoisted method name shadows a local.
        foreach (var localName in GetEnclosingMethodLocalVariableNames(localFn))
            combined.Add(localName);

        if (plan.PreserveOriginalName)
        {
            var originalName = localFn.Identifier.Text;
            // 成员名之外，方法名也不能与宿主类型同名（否则同名成员会变成构造函数，CS0542）。
            if (originalName != hostName && !combined.Contains(originalName))
            {
                usedNames.Add(originalName);
                return originalName;
            }

            // 原名与宿主类型成员冲突：只能用 camelCase 名。条件编译区里的引用
            // 必须跟着改，否则真实构建（该区域被启用时）会 CS0103。
            var renamed = LocalFunctionLiftNaming.ResolveUniqueName(baseName, combined);
            if (TryPlanDisabledTextRename(plan, localFn, originalName, renamed, out var reason))
            {
                usedNames.Add(renamed);
                return renamed;
            }

            Skip(plan, LocalFunctionSkipReason.NameReferencedInDisabledText,
                $"local function name '{originalName}' conflicts with an existing member of the host " +
                $"type and the conditionally compiled references cannot be renamed safely: {reason}");
            return null;
        }

        var finalName = LocalFunctionLiftNaming.ResolveUniqueName(baseName, combined);
        usedNames.Add(finalName);

        return finalName;
    }

    /// <summary>
    /// 原名与宿主成员冲突时，把条件编译区里指向本局部函数的标识符改名为提升方法名。
    /// 只有能确认「所有这些引用都属于本局部函数」时才允许替换：
    /// 引用都在声明所在的同一成员内，且文件里没有同名局部函数；
    /// 新名字也不能已经在禁用区里出现过（否则会误指向别的符号）。
    /// </summary>
    private bool TryPlanDisabledTextRename(
        LiftPlan plan,
        LocalFunctionStatementSyntax localFn,
        string originalName,
        string newName,
        out string reason)
    {
        reason = "";
        if (!_disabledTextReferences.TryGetValue(originalName, out var references)
            || references.Count == 0)
        {
            reason = "no conditionally compiled reference to the name was located";
            return false;
        }

        var member = localFn.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member == null)
        {
            reason = "the enclosing member of the local function was not found";
            return false;
        }

        if (_localFunctionNameCounts.GetValueOrDefault(originalName) != 1)
        {
            reason = "another local function in this file uses the same name";
            return false;
        }

        foreach (var reference in references)
        {
            if (reference.EnclosingMemberSpan != member.Span)
            {
                reason = "the name is also referenced from conditionally compiled text in another member";
                return false;
            }
        }

        if (DisabledTextContainsIdentifier(newName))
        {
            reason = $"the replacement name '{newName}' already occurs in conditionally compiled text";
            return false;
        }

        foreach (var reference in references)
        {
            plan.DisabledTextRenames.Add(new DisabledTextIdentifierRename(
                reference.TriviaPosition, reference.Offset, reference.Length, newName));
        }

        return true;
    }

    /// <summary>禁用区文本里是否已经出现该标识符（按标识符边界匹配）。</summary>
    private bool DisabledTextContainsIdentifier(string identifier)
    {
        foreach (var trivia in _disabledTextTexts)
        {
            var index = 0;
            while ((index = trivia.IndexOf(identifier, index, StringComparison.Ordinal)) >= 0)
            {
                var before = index == 0 || !SyntaxFacts.IsIdentifierPartCharacter(trivia[index - 1]);
                var afterIndex = index + identifier.Length;
                var after = afterIndex >= trivia.Length
                    || !SyntaxFacts.IsIdentifierPartCharacter(trivia[afterIndex]);
                if (before && after)
                    return true;
                index = afterIndex;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects all local variable and local constant names declared in the
    /// enclosing method (between the method start and the local function),
    /// so that the hoisted method name doesn't shadow them.
    /// </summary>
    private static HashSet<string> GetEnclosingMethodLocalVariableNames(
        LocalFunctionStatementSyntax localFn)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax)
                break;
            if (current is BaseMethodDeclarationSyntax method)
            {
                CollectLocalVariableNames(method, names);
                break;
            }
            current = current.Parent;
        }
        return names;
    }

    private static void CollectLocalVariableNames(
        SyntaxNode node, HashSet<string> names)
    {
        foreach (var decl in node.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>())
        {
            names.Add(decl.Identifier.Text);
        }
        foreach (var param in node.DescendantNodes()
            .OfType<ParameterSyntax>())
        {
            names.Add(param.Identifier.Text);
        }
    }
}
