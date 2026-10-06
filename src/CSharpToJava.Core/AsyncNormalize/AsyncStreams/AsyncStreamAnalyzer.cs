using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using CSharpToJava.Core.AsyncNormalize.AsyncStreams.AsyncIterator;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams;

/// <summary>
/// 单个 await using 资源的冻结计划（设计 §4.1/§4.3）。
/// </summary>
/// <param name="Declarator">语句/声明形态中显式声明的变量；纯表达式形态为 null。</param>
/// <param name="DeclaredTypeSyntax">显式声明形态的源类型语法（可能为 var）；纯表达式为 null。</param>
/// <param name="ResourceExpression">资源表达式（只求值一次，try 外原位保留）。</param>
/// <param name="Name">finally 释放引用的标识符（用户变量名或纯表达式临时量名）。</param>
/// <param name="IsReferenceType">引用类型资源 → <c>if (r != null)</c> 守卫（不做 nullability 流分析）。</param>
/// <param name="IsNullableValueType"><c>S?</c> 资源 → <c>if (r.HasValue)</c> 守卫、<c>r.Value</c> 接收者。</param>
/// <param name="UseInterfaceCast">显式接口实现等无公开模式方法 → <c>((IAsyncDisposable)r)</c> 转换调用。</param>
internal sealed record AwaitUsingResource(
    VariableDeclaratorSyntax? Declarator,
    TypeSyntax? DeclaredTypeSyntax,
    ExpressionSyntax ResourceExpression,
    string Name,
    bool IsReferenceType,
    bool IsNullableValueType,
    bool UseInterfaceCast);

/// <summary>
/// 一个 await using 站点（语句形态或 using 声明形态；多变量声明的每个声明符为一个资源，
/// 按声明序嵌套展开，禁止合并）。
/// </summary>
internal sealed record AwaitUsingSite(
    StatementSyntax Node,
    bool IsStatementForm,
    IReadOnlyList<AwaitUsingResource> Resources);

/// <summary>
/// C38 单文件分析结果。诊断一律 Error 发射，非严格降级由入口统一处理。
/// 注册序（§3.2）：AS-6 异步迭代器冻结 → AS-2 取消/ConfigureAwait 链消解 +
/// AS-1/3 await foreach 计划 → AS-4/5 await using 计划（3a）→ 类型使用点 → residual。
/// <see cref="ForeachRejectedNodes"/>（1840/1841/1843/1846）与
/// <see cref="ForeachBaselineExemptNodes"/>（CS1895/CS4033 族）在重写时原样保留并
/// 挂内存 skip 标注；<see cref="IteratorNodes"/> 整成员 skip（体内站点不做部分改写）。
/// </summary>
internal sealed record AsyncStreamAnalysisResult(
    IReadOnlyList<AsyncNormalizeDiagnostic> Diagnostics,
    IReadOnlyList<AwaitUsingSite> Sites,
    IReadOnlyList<AsyncForeachSite> ForeachSites,
    IReadOnlyList<SyntaxNode> IteratorNodes,
    IReadOnlyList<AsyncIteratorPlan> IteratorPlans,
    IReadOnlyList<CommonForEachStatementSyntax> ForeachRejectedNodes,
    IReadOnlyList<CommonForEachStatementSyntax> ForeachBaselineExemptNodes,
    IReadOnlyList<StatementSyntax> RejectedNodes,
    IReadOnlyList<StatementSyntax> BaselineExemptNodes,
    int StatementSitesAccepted,
    int DeclarationSitesAccepted,
    int ForeachSitesAccepted,
    int GuardsPlanned,
    int WithCancellationPlanned,
    int EnumerableConfigureAwaitPlanned,
    int IteratorsRejected,
    int TypeSitesMapped,
    int TypeSitesRejected,
    int DuckTypedDeferred);

/// <summary>
/// C38 发现面分析（设计 §3.2 注册序、§4.1、§4.4、§6、D2-D11）：
/// AS-6 异步迭代器冻结 → await foreach 站点（链消解/源类型判定）→ await using 站点
/// （3a）→ IAsync* 类型使用点；基线优先（Roslyn 已报的 CS1895/CS4033/CS8647 等不
/// 二次诊断）；计划在任何改写前冻结。
/// </summary>
internal static class AsyncStreamAnalyzer
{
    public static AsyncStreamAnalysisResult Analyze(
        SemanticModel model,
        AsyncNormalizeOptions options)
    {
        var diagnostics = new List<AsyncNormalizeDiagnostic>();
        var sites = new List<AwaitUsingSite>();
        var rejected = new List<StatementSyntax>();
        var baselineExempt = new List<StatementSyntax>();
        var foreachSites = new List<AsyncForeachSite>();
        var foreachRejected = new List<CommonForEachStatementSyntax>();
        var foreachBaseline = new List<CommonForEachStatementSyntax>();

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var asyncDisposableInterface =
            model.Compilation.GetTypeByMetadataName(AsyncTypeResolver.AsyncDisposableMetadataName);
        var asyncEnumerableInterface =
            model.Compilation.GetTypeByMetadataName(AsyncTypeResolver.AsyncEnumerableMetadataName);
        var asyncEnumeratorInterface =
            model.Compilation.GetTypeByMetadataName(AsyncTypeResolver.AsyncEnumeratorMetadataName);
        var disposableInterface =
            model.Compilation.GetSpecialType(SpecialType.System_IDisposable);

        var baselineErrors = CollectBaselineErrors(model);
        var dualImplInfoEmitted = new HashSet<string>(StringComparer.Ordinal);

        // 宿主成员作用域：已用名集合 + 纯表达式临时量序号（源序确定性）。
        // await using 与 await foreach 共享同一作用域命名账（前缀互不相交）。
        var scopeNamesByMember = new Dictionary<SyntaxNode, HashSet<string>>();
        var tempCountersByMember = new Dictionary<SyntaxNode, int>();

        var statementsAccepted = 0;
        var declarationsAccepted = 0;
        var foreachAccepted = 0;
        var guardsPlanned = 0;
        var typeSitesRejected = 0;
        var duckTypedDeferred = 0;
        var withCancellationPlanned = 0;
        var enumerableConfigureAwaitPlanned = 0;

        // ① AS-6 注册序最前：异步迭代器计划器。可降级方法不进冻结集（其体内
        // await foreach/await using 站点照常计划展开，之后由联合状态机 lowering 接管）；
        // 仅 v1 拒绝形态/基线已错成员整体冻结 skip（CS2J1844 直接进 diagnostics）。
        var iteratorAnalysis = AsyncIteratorPlanner.Analyze(
            model, options, asyncEnumerableInterface, asyncEnumeratorInterface, diagnostics);
        var iteratorNodes = iteratorAnalysis.FrozenNodes.ToArray();
        var iteratorsRejected = diagnostics.Count(d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1844);

        // ② AS-2/AS-1/AS-3：await foreach（含取消/ConfigureAwait 链消解）。
        foreach (var candidate in EnumerateAwaitForeachNodes(root))
        {
            if (ContainedInAny(candidate.Span, iteratorNodes))
                continue;

            var plan = BuildForeachSite(
                model, candidate, options, asyncEnumerableInterface, asyncEnumeratorInterface,
                baselineErrors, scopeNamesByMember, tempCountersByMember, diagnostics,
                foreachRejected, foreachBaseline,
                ref withCancellationPlanned, ref enumerableConfigureAwaitPlanned);

            if (plan != null)
            {
                foreachSites.Add(plan);
                foreachAccepted++;
            }
        }

        // ③ AS-4/AS-5：await using（3a 既有通道）。
        foreach (var candidate in EnumerateAwaitUsingNodes(root))
        {
            if (ContainedInAny(candidate.Span, iteratorNodes))
                continue;

            // 基线优先：站点跨度内已有编译错误（非 async 内 await＝CS4033、
            // switch section using 声明＝CS8647 等）不抢诊断、不改写，只挂白名单。
            if (BaselineContains(baselineErrors, model.SyntaxTree, candidate.Span))
            {
                baselineExempt.Add(candidate);
                continue;
            }

            // 声明形态只支持直接落在 Block（v1 可安全包尾到块末）；其余位置
            // （top-level statements 等）CS2J1845 防御报错、原样保留。
            if (candidate is LocalDeclarationStatementSyntax { Parent: not BlockSyntax })
            {
                rejected.Add(candidate);
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1845, candidate,
                    "'await using' declaration is only supported directly inside a block '{ }' " +
                    "(scope folding to the block end); declaration-form 'await using' in this " +
                    "host scope cannot be wrapped safely. Rewrite it as statement form or move it " +
                    "into an enclosing block."));
                continue;
            }

            var resourcesResult = BuildResources(
                model, candidate, options, asyncDisposableInterface, disposableInterface,
                scopeNamesByMember, tempCountersByMember,
                dualImplInfoEmitted, diagnostics,
                ref typeSitesRejected, ref duckTypedDeferred);

            if (resourcesResult == null)
            {
                // 资源被判 1841/1842/1845：站点原样保留，不做部分改写。
                rejected.Add(candidate);
                continue;
            }

            var isStatementForm = candidate is UsingStatementSyntax;
            sites.Add(new AwaitUsingSite(candidate, isStatementForm, resourcesResult));
            if (isStatementForm)
                statementsAccepted++;
            else
                declarationsAccepted++;
            guardsPlanned += resourcesResult.Count(r => r.IsReferenceType || r.IsNullableValueType);
        }

        // ④ AS-7/AS-8：类型使用点（放行/1841/1843；迭代器成员跨度内不计不判）。
        var typeSitesMapped = CountAsyncTypeSites(
            model, root, options,
            asyncDisposableInterface, asyncEnumerableInterface, asyncEnumeratorInterface,
            iteratorNodes, diagnostics, ref typeSitesRejected);

        return new AsyncStreamAnalysisResult(
            diagnostics,
            sites,
            foreachSites,
            iteratorNodes,
            iteratorAnalysis.Plans,
            foreachRejected,
            foreachBaseline,
            rejected,
            baselineExempt,
            statementsAccepted,
            declarationsAccepted,
            foreachAccepted,
            guardsPlanned,
            withCancellationPlanned,
            enumerableConfigureAwaitPlanned,
            iteratorsRejected,
            typeSitesMapped,
            typeSitesRejected,
            duckTypedDeferred);
    }

    // ── await foreach：发现 / 链消解 / 源判定（AS-1/AS-2/AS-3、D2/D3/D9） ──

    private static IEnumerable<CommonForEachStatementSyntax> EnumerateAwaitForeachNodes(
        CompilationUnitSyntax root)
    {
        foreach (var node in root.DescendantNodes()
                     .OfType<CommonForEachStatementSyntax>())
        {
            if (node.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
                yield return node;
        }
    }

    private static AsyncForeachSite? BuildForeachSite(
        SemanticModel model,
        CommonForEachStatementSyntax candidate,
        AsyncNormalizeOptions options,
        INamedTypeSymbol? asyncEnumerableInterface,
        INamedTypeSymbol? asyncEnumeratorInterface,
        List<(SyntaxTree Tree, TextSpan Span)> baselineErrors,
        Dictionary<SyntaxNode, HashSet<string>> scopeNamesByMember,
        Dictionary<SyntaxNode, int> tempCountersByMember,
        List<AsyncNormalizeDiagnostic> diagnostics,
        List<CommonForEachStatementSyntax> foreachRejected,
        List<CommonForEachStatementSyntax> foreachBaseline,
        ref int withCancellationPlanned,
        ref int enumerableConfigureAwaitPlanned)
    {
        // 基线优先：非 async 方法内 await（CS4033）、对 IEnumerable/数组/string 源写
        // await（CS1895 族）等 Roslyn 已报形态不抢诊断。
        if (BaselineContains(baselineErrors, model.SyntaxTree, candidate.Span))
        {
            foreachBaseline.Add(candidate);
            return null;
        }

        // 残余解构形态（E06/E07 之后仍应见不到）：v1 防御 CS2J1840，不受选项门影响。
        if (candidate is ForEachVariableStatementSyntax)
        {
            foreachRejected.Add(candidate);
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1840, candidate,
                "deconstruction-form 'await foreach' ('await foreach (var (k, v) in …)') is " +
                "not supported in v1; the residual deconstruction shape could not be bound to " +
                "a single element type. The site is left unchanged."));
            return null;
        }

        var forEach = (ForEachStatementSyntax)candidate;

        // 选项门：EnableAwaitForeach=false → CS2J1841（含 IAsyncEnumerable 源站点）。
        if (!options.EnableAwaitForeach)
        {
            foreachRejected.Add(candidate);
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1841, candidate,
                "'await foreach' cannot be normalized because the EnableAwaitForeach option is " +
                "disabled. Enable the option, or wait for the Java-side async-enumerable " +
                "bridge. The site is left unchanged."));
            return null;
        }

        // AS-2 链消解：在源表达式上由内而外剥 WithCancellation / ConfigureAwait。
        var chain = ResolveSourceChain(model, forEach, diagnostics, candidate);
        if (chain.Rejected)
        {
            foreachRejected.Add(candidate);
            return null;
        }

        var receiverType = model.GetTypeInfo(chain.Receiver).Type
            ?? model.GetTypeInfo(chain.Receiver).ConvertedType;

        var shape = AsyncTypeResolver.GetAsyncStreamStreamShape(
            receiverType, asyncEnumerableInterface, asyncEnumeratorInterface);

        switch (shape)
        {
            case AsyncTypeResolver.AsyncStreamShape.ConfiguredCancelable:
                // 强类型 ConfiguredCancelableAsyncEnumerable<T> 源：剥离链会改静态类型。
                // 该局部/参数/字段的类型使用点通常已在类型面发 1843；未覆盖时在此补一条。
                foreachRejected.Add(candidate);
                if (!TypeSiteDiagnosticCovers(model, forEach.Expression))
                {
                    diagnostics.Add(DiagnosticAt(
                        AsyncNormalizeDiagnosticCode.CS2J1843, candidate,
                        $"'await foreach' source is strongly typed " +
                        $"'{receiverType!.ToDisplayString()}' (ConfiguredCancelableAsyncEnumerable<T>); " +
                        "stripping the configured/cancelable wrapper changes the static type and " +
                        "is not supported in v1. Materialize it via a WithCancellation/ConfigureAwait " +
                        "chain directly on the IAsyncEnumerable<T> source."));
                }
                return null;

            case AsyncTypeResolver.AsyncStreamShape.Enumerator:
                // IAsyncEnumerator<T> 直连（无 GetAsyncEnumerator 可绑）＝基线 CS1895 族：
                // 即使基线快照漏报也不抢诊断（D9）。
                foreachBaseline.Add(candidate);
                return null;

            case AsyncTypeResolver.AsyncStreamShape.Other:
                // 鸭子型（有 GetAsyncEnumerator 但不实现接口）→ CS2J1840；其余异类同码防御。
                foreachRejected.Add(candidate);
                var kind = AsyncTypeResolver.HasPatternGetAsyncEnumerator(receiverType)
                    ? "provides a pattern-based (duck-typed) 'GetAsyncEnumerator' but does not " +
                      "implement 'System.Collections.Generic.IAsyncEnumerable<T>'"
                    : "does not implement 'System.Collections.Generic.IAsyncEnumerable<T>'";
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1840, candidate,
                    $"'await foreach' source type '{receiverType?.ToDisplayString() ?? "<unknown>"}' {kind}; " +
                    "only IAsyncEnumerable<T> sources are normalized in v1. The site is left unchanged."));
                return null;
        }

        // Enumerable：冻结计划（临时名走 AsyncStreamNaming，成员作用域防撞）。
        var elementSymbol = model.GetDeclaredSymbol(forEach) as ILocalSymbol;
        var scopeMember = GetScopeMember(forEach);
        var usedNames = GetOrCreateScopeNames(scopeNamesByMember, scopeMember, forEach);
        var index = tempCountersByMember.TryGetValue(scopeMember, out var currentIndex)
            ? currentIndex
            : 0;
        tempCountersByMember[scopeMember] = index + 1;
        var enumeratorName = AsyncStreamNaming.AllocateEnumeratorTempName(index, usedNames);
        var okName = AsyncStreamNaming.AllocateOkTempName(index, usedNames);

        if (chain.HasWithCancellation)
            withCancellationPlanned++;
        enumerableConfigureAwaitPlanned += chain.ConfigureAwaitCount;

        return new AsyncForeachSite(
            forEach,
            chain.Receiver,
            chain.Token,
            enumeratorName,
            okName,
            elementSymbol?.Type,
            forEach.Type.IsVar,
            chain.HasWithCancellation,
            chain.ConfigureAwaitCount);
    }

    /// <summary>
    /// 取消/ConfigureAwait 链消解结果（设计 D3 五分项）。裸源 token 恒显式
    /// <c>global::System.Threading.CancellationToken.None</c>，消除默认参数魔法。
    /// </summary>
    private sealed record ChainResolution(
        ExpressionSyntax Receiver,
        ExpressionSyntax Token,
        bool HasWithCancellation,
        int ConfigureAwaitCount,
        bool Rejected);

    private static ChainResolution ResolveSourceChain(
        SemanticModel model,
        ForEachStatementSyntax forEach,
        List<AsyncNormalizeDiagnostic> diagnostics,
        CommonForEachStatementSyntax diagnosticSite)
    {
        var current = forEach.Expression;
        ExpressionSyntax token = SyntaxFactory.ParseExpression(
            "global::System.Threading.CancellationToken.None");
        var sawWithCancellation = false;
        var configureAwaitCount = 0;

        while (current is InvocationExpressionSyntax invocation
            && invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
            {
                // 不可静态绑定：多为自定义链/基线错误外的漏网，宁拒不错（D3⑤/风险 10）。
                Reject(AsyncNormalizeDiagnosticCode.CS2J1846,
                    "an invocation in the 'await foreach' source chain could not be statically " +
                    "bound; only BCL 'WithCancellation(CancellationToken)' / 'ConfigureAwait(bool)' " +
                    "chains on IAsyncEnumerable<T> can be resolved in v1.");
                return RejectedChain(forEach.Expression);
            }

            if (AsyncTypeResolver.IsBclWithCancellation(method))
            {
                if (sawWithCancellation)
                {
                    Reject(AsyncNormalizeDiagnosticCode.CS2J1846,
                        "repeated 'WithCancellation' in the 'await foreach' source chain is not " +
                        "supported in v1; pass a single CancellationToken.");
                    return RejectedChain(forEach.Expression);
                }

                if (invocation.ArgumentList.Arguments.Count != 1)
                {
                    Reject(AsyncNormalizeDiagnosticCode.CS2J1846,
                        "'WithCancellation' must take exactly one argument.");
                    return RejectedChain(forEach.Expression);
                }

                var argument = invocation.ArgumentList.Arguments[0].Expression;
                var argumentType = model.GetTypeInfo(argument).Type;
                if (!AsyncTypeResolver.IsCancellationTokenTyped(argumentType))
                {
                    Reject(AsyncNormalizeDiagnosticCode.CS2J1846,
                        $"'WithCancellation' argument of type '{argumentType?.ToDisplayString() ?? "<unknown>"}' " +
                        "is not a statically bindable 'System.Threading.CancellationToken'; only " +
                        "CancellationToken arguments are forwarded to GetAsyncEnumerator in v1.");
                    return RejectedChain(forEach.Expression);
                }

                sawWithCancellation = true;
                token = argument;
                current = memberAccess.Expression;
                continue;
            }

            if (AsyncTypeResolver.IsBclAsyncStreamConfigureAwait(method))
            {
                if (invocation.ArgumentList.Arguments.Count != 1)
                {
                    Reject(AsyncNormalizeDiagnosticCode.CS2J1843,
                        "async-stream 'ConfigureAwait' must take exactly one bool argument.");
                    return RejectedChain(forEach.Expression);
                }

                var argument = invocation.ArgumentList.Arguments[0].Expression;
                var argumentType = model.GetTypeInfo(argument).Type;
                if (argumentType is null || argumentType.SpecialType != SpecialType.System_Boolean)
                {
                    // 枚举器/枚举上非 bool ConfigureAwait（含 options 形态）：CS2J1843。
                    Reject(AsyncNormalizeDiagnosticCode.CS2J1843,
                        $"'ConfigureAwait' argument of type '{argumentType?.ToDisplayString() ?? "<unknown>"}' " +
                        "on an async-enumerable chain is not the bool form; the options/non-bool " +
                        "shape changes the configured enumerator type and is not supported in v1.");
                    return RejectedChain(forEach.Expression);
                }

                configureAwaitCount++;
                current = memberAccess.Expression;
                continue;
            }

            if (method.Name is "WithCancellation" or "ConfigureAwait")
            {
                // 同名但绑定到非 BCL 符号（用户自定义扩展）：宁拒不错（风险 10）。
                Reject(AsyncNormalizeDiagnosticCode.CS2J1846,
                    $"'{method.Name}' in the 'await foreach' source chain binds to non-BCL symbol " +
                    $"'{method.ContainingType.ToDisplayString()}'; only the BCL extensions are accepted.");
                return RejectedChain(forEach.Expression);
            }

            break;

            void Reject(AsyncNormalizeDiagnosticCode code, string message)
                => diagnostics.Add(DiagnosticAt(code, diagnosticSite, message));
        }

        if (configureAwaitCount > 0)
        {
            var line = diagnosticSite.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            diagnostics.Add(new AsyncNormalizeDiagnostic(
                AsyncNormalizeSeverity.Info,
                AsyncNormalizeDiagnosticCode.CS2J1830,
                $"Stripped {configureAwaitPlural(configureAwaitCount)} on the async-enumerable source " +
                "(Java has no SynchronizationContext; the 'await' sites are preserved).",
                Line: line));
        }

        return new ChainResolution(current, token, sawWithCancellation, configureAwaitCount, false);

        static string configureAwaitPlural(int n) => n == 1
            ? "one 'ConfigureAwait(bool)' chain"
            : $"{n} 'ConfigureAwait(bool)' chains";
    }

    private static ChainResolution RejectedChain(ExpressionSyntax original)
        => new(original,
            SyntaxFactory.ParseExpression("global::System.Threading.CancellationToken.None"),
            false, 0, Rejected: true);

    /// <summary>
    /// 强类型 ConfiguredCancelable 诊断是否已由类型使用面发出并覆盖源表达式跨度
    /// （避免同一强类型局部/参数/字段在 foreach 站点重复发 1843）。
    /// </summary>
    private static bool TypeSiteDiagnosticCovers(SemanticModel model, ExpressionSyntax source)
    {
        // 源若直接是标识符/成员访问，其声明点的强类型诊断已覆盖；其余形态（调用/转型等）
        // 无声明点可挂，由 foreach 站点补诊断。
        var symbol = model.GetSymbolInfo(source).Symbol
            ?? (model.GetSymbolInfo(source).CandidateSymbols.FirstOrDefault());
        if (symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol)
            return true;
        return false;
    }

    // ── 站点与资源构建 ──

    private static IEnumerable<StatementSyntax> EnumerateAwaitUsingNodes(CompilationUnitSyntax root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case UsingStatementSyntax usingStatement
                    when usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    yield return usingStatement;
                    break;
                case LocalDeclarationStatementSyntax local
                    when local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                         && local.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    yield return local;
                    break;
            }
        }
    }

    private static IReadOnlyList<AwaitUsingResource>? BuildResources(
        SemanticModel model,
        StatementSyntax candidate,
        AsyncNormalizeOptions options,
        INamedTypeSymbol? asyncDisposableInterface,
        INamedTypeSymbol disposableInterface,
        Dictionary<SyntaxNode, HashSet<string>> scopeNamesByMember,
        Dictionary<SyntaxNode, int> tempCountersByMember,
        HashSet<string> dualImplInfoEmitted,
        List<AsyncNormalizeDiagnostic> diagnostics,
        ref int typeSitesRejected,
        ref int duckTypedDeferred)
    {
        var declarators = new List<(VariableDeclaratorSyntax? Declarator, TypeSyntax? TypeSyntax,
            ExpressionSyntax? Expression, ITypeSymbol? Type)>();

        switch (candidate)
        {
            case UsingStatementSyntax { Declaration: { } declaration }:
                foreach (var declarator in declaration.Variables)
                {
                    var type = (model.GetDeclaredSymbol(declarator) as ILocalSymbol)?.Type;
                    declarators.Add((declarator, declaration.Type,
                        declarator.Initializer?.Value, type));
                }
                break;

            case UsingStatementSyntax { Expression: { } expression }:
                var expressionType = model.GetTypeInfo(expression).Type
                    ?? model.GetTypeInfo(expression).ConvertedType;
                declarators.Add((null, null, expression, expressionType));
                break;

            case LocalDeclarationStatementSyntax localDeclaration:
                foreach (var declarator in localDeclaration.Declaration.Variables)
                {
                    var type = (model.GetDeclaredSymbol(declarator) as ILocalSymbol)?.Type;
                    declarators.Add((declarator, localDeclaration.Declaration.Type,
                        declarator.Initializer?.Value, type));
                }
                break;

            default:
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1845, candidate,
                    "unrecognized 'await using' site shape; the node is left unchanged."));
                return null;
        }

        if (declarators.Count == 0
            || declarators.Any(d => d.Expression == null || d.Type is not { } type))
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1845, candidate,
                "'await using' resource could not be bound to a type or has no initializer; " +
                "the site is left unchanged."));
            return null;
        }

        var resources = new List<AwaitUsingResource>(declarators.Count);
        var siteRejected = false;

        foreach (var (declarator, declaredTypeSyntax, expression, type) in declarators)
        {
            var resourceType = type!;
            var resourceExpression = expression!;

            // ① ref struct（含其鸭子型 DisposeAsync）：v1 不吞，CS2J1842。
            if (AsyncTypeResolver.IsRefLike(resourceType))
            {
                duckTypedDeferred++;
                siteRejected = true;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1842, candidate,
                    $"'await using' resource type '{resourceType.ToDisplayString()}' is a ref struct " +
                    "with pattern-based async disposal; ref struct / pattern-based (duck-typed) " +
                    "'DisposeAsync' is not supported in v1. Only resources implementing " +
                    "'System.IAsyncDisposable' are normalized."));
                continue;
            }

            // ①-b BCL ConfiguredAsyncDisposable（IAsyncDisposable.ConfigureAwait(bool)）：
            // 不是用户鸭子型，不能给"显式实现接口"的错误建议，单独 CS2J1847。
            if (AsyncTypeResolver.IsConfiguredAsyncDisposable(resourceType))
            {
                siteRejected = true;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1847, candidate,
                    "'await using' resource is a BCL 'ConfiguredAsyncDisposable' produced by " +
                    "'IAsyncDisposable.ConfigureAwait(bool)'; unwrapping the configured-disposable " +
                    "shape is not supported in v1 (Java has no SynchronizationContext to " +
                    "configure). Drop the '.ConfigureAwait(...)' call on the resource expression."));
                continue;
            }

            // ② 接口资源：选项关 → CS2J1841；双实现 → Info 一次，仍绑 DisposeAsync（D8）。
            if (AsyncTypeResolver.IsAsyncDisposable(resourceType, asyncDisposableInterface))
            {
                if (!options.EnableAwaitUsing)
                {
                    typeSitesRejected++;
                    siteRejected = true;
                    diagnostics.Add(DiagnosticAt(
                        AsyncNormalizeDiagnosticCode.CS2J1841, candidate,
                        $"'await using' resource of type '{resourceType.ToDisplayString()}' cannot " +
                        "be normalized because the EnableAwaitUsing option is disabled. Enable the " +
                        "option, or wait for the Java-side IAsyncDisposable bridge."));
                    continue;
                }

                RecordDualImplementationInfoOnce(
                    resourceType, disposableInterface, candidate, dualImplInfoEmitted, diagnostics);

                resources.Add(CreateResource(
                    declarator, declaredTypeSyntax, resourceExpression, resourceType,
                    candidate, scopeNamesByMember, tempCountersByMember));
                continue;
            }

            // ③ 鸭子型（无接口、有可绑定 DisposeAsync）：CS2J1842。
            if (AsyncTypeResolver.IsPatternBasedAsyncDisposable(resourceType))
            {
                duckTypedDeferred++;
                siteRejected = true;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1842, candidate,
                    $"'await using' resource type '{resourceType.ToDisplayString()}' only provides a " +
                    "pattern-based (duck-typed) 'DisposeAsync' and does not implement " +
                    "'System.IAsyncDisposable'; pattern-based async disposal is not supported in " +
                    "v1 (implement the interface explicitly)."));
                continue;
            }

            // ④ 既非接口也非模式：基线通常已报（CS1848 族）；走到这里属漏网，防御镜像。
            siteRejected = true;
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1845, candidate,
                $"'await using' resource type '{resourceType.ToDisplayString()}' neither implements " +
                "'System.IAsyncDisposable' nor provides a bindable pattern-based 'DisposeAsync'; " +
                "the site is left unchanged."));
        }

        return siteRejected ? null : resources;
    }

    private static AwaitUsingResource CreateResource(
        VariableDeclaratorSyntax? declarator,
        TypeSyntax? declaredTypeSyntax,
        ExpressionSyntax resourceExpression,
        ITypeSymbol resourceType,
        StatementSyntax site,
        Dictionary<SyntaxNode, HashSet<string>> scopeNamesByMember,
        Dictionary<SyntaxNode, int> tempCountersByMember)
    {
        string name;
        if (declarator != null)
        {
            name = declarator.Identifier.ValueText;
        }
        else
        {
            var scopeMember = GetScopeMember(site);
            var usedNames = GetOrCreateScopeNames(scopeNamesByMember, scopeMember, site);
            var index = tempCountersByMember.TryGetValue(scopeMember, out var current) ? current : 0;
            tempCountersByMember[scopeMember] = index + 1;
            name = AsyncStreamNaming.AllocateResourceTempName(index, usedNames);
        }

        return new AwaitUsingResource(
            declarator,
            declaredTypeSyntax,
            resourceExpression,
            name,
            IsReferenceType: resourceType.IsReferenceType,
            IsNullableValueType: AsyncTypeResolver.IsNullableValueType(resourceType),
            UseInterfaceCast: !AsyncTypeResolver.HasAccessiblePatternDispose(resourceType));
    }

    private static void RecordDualImplementationInfoOnce(
        ITypeSymbol resourceType,
        INamedTypeSymbol disposableInterface,
        StatementSyntax site,
        HashSet<string> infoEmitted,
        List<AsyncNormalizeDiagnostic> diagnostics)
    {
        if (!AsyncTypeResolver.ImplementsSyncDisposable(resourceType, disposableInterface))
            return;

        var display = resourceType.OriginalDefinition.ToDisplayString();
        if (!infoEmitted.Add(display))
            return;

        var line = site.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Info,
            AsyncNormalizeDiagnosticCode.CS2J1830,
            $"Type '{display}' implements both System.IDisposable and System.IAsyncDisposable; " +
            "'await using' binds 'DisposeAsync' (sync 'using' still binds 'Dispose').",
            Line: line));
    }

    // ── IAsync* 类型使用点（非语句位放行/拒绝，零改写，设计 §2.2 归宿矩阵、D5/D9） ──

    /// <summary>
    /// 扫描参数/字段/属性/返回/局部上的异步流类型使用点：
    /// <list type="bullet">
    /// <item>IAsyncDisposable：恒放行计数（3a 纪律；EnableAwaitForeach 门不波及）。</item>
    /// <item>IAsyncEnumerable&lt;T&gt;/IAsyncEnumerator&lt;T&gt;：EnableAwaitForeach 放行计数，
    /// 否则逐点 CS2J1841。</item>
    /// <item>ConfiguredCancelableAsyncEnumerable&lt;T&gt;：恒 CS2J1843（剥离改静态类型，v1 拒绝）。</item>
    /// </list>
    /// AS-6 冻结成员跨度内（含其返回类型）不计不判；await using 资源位与 foreach 元素位
    /// 不经此通道（它们由站点计划处理）。
    /// </summary>
    private static int CountAsyncTypeSites(
        SemanticModel model,
        CompilationUnitSyntax root,
        AsyncNormalizeOptions options,
        INamedTypeSymbol? asyncDisposableInterface,
        INamedTypeSymbol? asyncEnumerableInterface,
        INamedTypeSymbol? asyncEnumeratorInterface,
        IReadOnlyList<SyntaxNode> iteratorNodes,
        List<AsyncNormalizeDiagnostic> diagnostics,
        ref int typeSitesRejected)
    {
        var count = 0;
        var rejectedLocal = 0;

        void Consider(SyntaxNode siteSyntax, ITypeSymbol? type)
        {
            if (type is null)
                return;
            if (ContainedInAny(siteSyntax.Span, iteratorNodes))
                return;

            if (AsyncTypeResolver.IsConfiguredCancelableAsyncEnumerable(type))
            {
                rejectedLocal++;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1843, siteSyntax,
                    $"Strongly-typed 'System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable<T>' " +
                    $"use site ('{type.ToDisplayString()}') is not supported in v1; stripping the " +
                    "configured/cancelable wrapper changes the static type. Apply " +
                    "'WithCancellation'/'ConfigureAwait' at the 'await foreach' site instead."));
                return;
            }

            if (AsyncTypeResolver.IsConfiguredAsyncDisposable(type))
            {
                rejectedLocal++;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1847, siteSyntax,
                    $"Strongly-typed 'System.Runtime.CompilerServices.ConfiguredAsyncDisposable' " +
                    $"use site ('{type.ToDisplayString()}') is not supported in v1; the result of " +
                    "'IAsyncDisposable.ConfigureAwait(bool)' is a pattern-based struct that cannot " +
                    "be mapped to 'System.IAsyncDisposable'. Drop '.ConfigureAwait(...)' and keep " +
                    "the IAsyncDisposable-typed value."));
                return;
            }

            if (AsyncTypeResolver.IsAsyncDisposable(type, asyncDisposableInterface))
            {
                count++;
                return;
            }

            if (AsyncTypeResolver.IsAsyncEnumerable(type, asyncEnumerableInterface)
                || AsyncTypeResolver.IsAsyncEnumerator(type, asyncEnumeratorInterface))
            {
                if (options.EnableAwaitForeach)
                {
                    count++;
                }
                else
                {
                    rejectedLocal++;
                    diagnostics.Add(DiagnosticAt(
                        AsyncNormalizeDiagnosticCode.CS2J1841, siteSyntax,
                        $"Async-stream type use site ('{type.ToDisplayString()}') cannot be normalized " +
                        "because the EnableAwaitForeach option is disabled. Enable the option, or wait " +
                        "for the Java-side async-enumerable bridge. The site is left unchanged."));
                }
            }
        }

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (model.GetDeclaredSymbol(parameter) is IParameterSymbol symbol)
                Consider(parameter, symbol.Type);
        }

        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var declarator in field.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(declarator) is IFieldSymbol symbol)
                    Consider(declarator, symbol.Type);
            }
        }

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(property) is IPropertySymbol symbol)
                Consider(property.Type, symbol.Type);
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(method) is IMethodSymbol symbol)
                Consider(method.ReturnType, symbol.ReturnType);
        }

        foreach (var localFunction in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (model.GetDeclaredSymbol(localFunction) is IMethodSymbol symbol)
                Consider(localFunction.ReturnType, symbol.ReturnType);
        }

        // 局部（排除 using 声明资源与 foreach 元素变量——那些走语句位）。
        foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                continue;
            foreach (var declarator in local.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(declarator) is ILocalSymbol symbol)
                    Consider(declarator, symbol.Type);
            }
        }

        typeSitesRejected += rejectedLocal;
        return count;
    }

    // ── 作用域 / 基线快照 ──

    private static SyntaxNode GetScopeMember(StatementSyntax site)
    {
        for (var node = site.Parent; node != null; node = node.Parent)
        {
            switch (node)
            {
                case MethodDeclarationSyntax:
                case ConstructorDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                case AccessorDeclarationSyntax:
                case OperatorDeclarationSyntax:
                case ConversionOperatorDeclarationSyntax:
                case DestructorDeclarationSyntax:
                    return node;
                case CompilationUnitSyntax:
                    return node;
            }
        }

        return site.SyntaxTree.GetRoot();
    }

    private static HashSet<string> GetOrCreateScopeNames(
        Dictionary<SyntaxNode, HashSet<string>> cache,
        SyntaxNode scopeMember,
        StatementSyntax site)
    {
        if (cache.TryGetValue(scopeMember, out var existing))
            return existing;

        // CollectScopeNames 自身按站点祖先解析成员作用域，scopeMember 仅作缓存键。
        var used = AsyncStreamNaming.CollectScopeNames(site);
        cache[scopeMember] = used;
        return used;
    }

    private static List<(SyntaxTree Tree, TextSpan Span)> CollectBaselineErrors(SemanticModel model)
    {
        var result = new List<(SyntaxTree, TextSpan)>();
        foreach (var diagnostic in model.Compilation.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error) continue;
            if (diagnostic.Location.SourceTree is not { } sourceTree) continue;
            result.Add((sourceTree, diagnostic.Location.SourceSpan));
        }
        return result;
    }

    private static bool BaselineContains(
        List<(SyntaxTree Tree, TextSpan Span)> baselineErrors,
        SyntaxTree tree,
        TextSpan outerSpan)
        => baselineErrors.Any(e =>
            e.Tree == tree
            && e.Span.Start >= outerSpan.Start
            && e.Span.End <= outerSpan.End);

    private static bool ContainedInAny(TextSpan span, IReadOnlyList<SyntaxNode> members)
        => members.Any(m => m.Span.Contains(span));

    private static AsyncNormalizeDiagnostic DiagnosticAt(
        AsyncNormalizeDiagnosticCode code,
        SyntaxNode node,
        string message)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Error,
            code,
            message,
            Line: line);
    }
}
