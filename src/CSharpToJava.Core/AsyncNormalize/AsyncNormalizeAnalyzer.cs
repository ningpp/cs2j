using CSharpToJava.Core.AsyncNormalize.Lambda;
using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// 整树发现与合法性分析（设计 §2 D3/D4/D5/D6/D10/D15、§4.1、§6）。
/// 语法粗筛 + SemanticModel 符号双判；基线优先——Roslyn 已报的不二次报错。
/// skip 集在任何改写前确定（静态、幂等）。诊断一律以 Error 级发射，非严格降级由入口统一处理。
/// </summary>
internal static class AsyncNormalizeAnalyzer
{
    private const string TaskDisplayName = "global::System.Threading.Tasks.Task";

    public static AsyncAnalysisResult Analyze(SemanticModel model, AsyncNormalizeOptions options)
    {
        var diagnostics = new List<AsyncNormalizeDiagnostic>();
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

        var baselineLockErrors = CollectBaselineLockErrors(model);
        var baselineUnsafeAwaitErrors = CollectBaselineUnsafeAwaitErrors(model);
        var baselineAllErrors = options.LiftAsyncLambdas
            ? CollectBaselineErrors(model)
            : new List<(SyntaxTree Tree, TextSpan Span)>();

        var awaitSites = 0;
        var unsupportedAwaiters = 0;
        var asyncMethodsSeen = 0;
        var asyncVoidCount = 0;

        // ① async 方法 / 残余 async 局部函数 + async void（D6）
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(method) is not IMethodSymbol symbol) continue;
            if (!symbol.IsAsync) continue;
            asyncMethodsSeen++;
            if (options.RejectAsyncVoid && symbol.ReturnsVoid)
            {
                asyncVoidCount++;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1803, method,
                    $"User-written async void method '{symbol.Name}' is not supported: unobserved " +
                    "exceptions and fire-and-forget semantics have no Java-side contract. Change it " +
                    "to 'async Task' and discard at the call site, or use the pass-generated " +
                    "synchronous wrapper form."));
            }
        }

        foreach (var localFunction in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (model.GetDeclaredSymbol(localFunction) is not IMethodSymbol symbol) continue;
            if (!symbol.IsAsync) continue;
            asyncMethodsSeen++;
            if (options.RejectAsyncVoid && symbol.ReturnsVoid)
            {
                asyncVoidCount++;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1803, localFunction,
                    $"User-written async void local function '{symbol.Name}' is not supported: " +
                    "unobserved exceptions and fire-and-forget semantics have no Java-side " +
                    "contract. Change it to 'async Task'."));
            }
        }

        // ② 全部 await 站点：计数 + dynamic/自定义 awaitable（D15）
        foreach (var awaitExpression in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            awaitSites++;

            if (AsyncShapeClassifier.IsAwaitDynamic(model, awaitExpression))
            {
                unsupportedAwaiters++;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1801, awaitExpression,
                    "await on a 'dynamic' expression is not supported: the awaiter contract " +
                    "cannot be determined from the SemanticModel."));
                continue;
            }

            var awaitedType = AsyncShapeClassifier.GetAwaitableType(awaitExpression, model);
            if (!AsyncShapeClassifier.IsTaskLike(awaitedType)
                && AsyncShapeClassifier.IsCustomAwaitable(awaitedType, model))
            {
                unsupportedAwaiters++;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1801, awaitExpression,
                    $"await on custom awaitable type '{awaitedType!.ToDisplayString()}' is not " +
                    "supported by the lightweight normalizer; only Task/ValueTask shapes are " +
                    "mapped on the Java side."));
            }
        }

        // ③ ConfigureAwait 调用：可归一化的独立 await options 形态放行（由 rewriter 改写）；
        //    其余 options 形态 → CS2J1808；bool 形态交 AwaitShapeRewriter 剥离。
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!AsyncShapeClassifier.IsConfigureAwaitBool(model, invocation, out var isOptionsForm))
                continue;
            if (isOptionsForm)
            {
                // 独立 await 表达式语句 + options 为编译期常量：无 SuppressThrowing → 剥离为
                // ConfigureAwait(false)；含 SuppressThrowing → try { await ... } catch { }。
                if (AsyncShapeClassifier.IsNormalizableOptionsAwait(model, invocation, out _))
                    continue;

                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1808, invocation,
                    "ConfigureAwait(ConfigureAwaitOptions) cannot be normalized: the options form " +
                    "(e.g. SuppressThrowing) has no Java-side equivalent unless it is a standalone " +
                    "'await ...;' statement with a constant options value."));
            }
        }

        // ④ 强类型 ConfiguredTaskAwaitable/ConfiguredValueTaskAwaitable 声明面 → CS2J1808（D5）
        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(declarator);
            ITypeSymbol? declaredType = symbol switch
            {
                ILocalSymbol local => local.Type,
                IFieldSymbol field => field.Type,
                _ => null,
            };
            if (declaredType != null && AsyncShapeClassifier.IsConfiguredAwaitable(declaredType))
            {
                var kind = symbol is IFieldSymbol ? "field" : "local";
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1808, declarator,
                    $"Strongly typed '{declaredType.ToDisplayString()}' {kind} '{symbol!.Name}' " +
                    "cannot be normalized: unwrapping the configured awaitable changes the type " +
                    "and requires per-site judgment."));
            }
        }

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (model.GetDeclaredSymbol(parameter) is not IParameterSymbol parameterSymbol) continue;
            if (AsyncShapeClassifier.IsConfiguredAwaitable(parameterSymbol.Type))
            {
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1808, parameter,
                    $"Strongly typed '{parameterSymbol.Type.ToDisplayString()}' parameter " +
                    $"'{parameterSymbol.Name}' cannot be normalized: unwrapping the configured " +
                    "awaitable changes the type and requires per-site judgment."));
            }
        }

        // ⑤ lock 体内 await：基线 CS1996 优先，基线未拦截才防御镜像 CS2J1805（D10）
        foreach (var lockStatement in root.DescendantNodes().OfType<LockStatementSyntax>())
        {
            var hasAwait = lockStatement.Statement
                .DescendantNodes()
                .OfType<AwaitExpressionSyntax>()
                .Any();
            if (!hasAwait) continue;
            if (BaselineContains(baselineLockErrors, model.SyntaxTree, lockStatement.Span))
                continue;

            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1805, lockStatement,
                "await inside a lock statement (defensive mirror of CS1996; the baseline " +
                "compiler error takes precedence when already reported)."));
        }

        // ⑤b unsafe/fixed 体内 await：基线 CS4004 优先，基线漏报才防御镜像 CS2J1809
        // （设计 §4.6 CS2J1805 条目原注：unsafe/fixed 编号 M0 回填；M0 探针确认
        // unsafe 块 / fixed / stackalloc 不安全上下文中的 await 一律 CS4004）。
        foreach (var awaitExpression in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            if (!awaitExpression.Ancestors()
                    .Any(a => a is UnsafeStatementSyntax or FixedStatementSyntax))
                continue;
            if (BaselineContains(baselineUnsafeAwaitErrors, model.SyntaxTree, awaitExpression.Span))
                continue;

            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1809, awaitExpression,
                "await inside an unsafe/fixed context (defensive mirror of CS4004; the " +
                "baseline compiler error takes precedence when already reported). Pinning/" +
                "pointer state cannot survive suspension."));
        }

        // ⑤c ref struct/ref local 跨 await 不设镜像（探针 2026-10-06 裁决，见审计记录）：
        // Roslyn 5（转换器内置编译器）全语言版本均不发 CS4007/CS9217——C# 13 起 ref struct
        // 局部跨 await 合法（旧语言版本由声明面 CS8936/CS9058/CS9202 拦截）。基线无洞，
        // 且阻塞式 await 下 ref struct 局部在 Java 侧就是普通局部，无能力缺口，不制造假诊断。

        // ⑥ async lambda / 残余 async 局部函数 / async 匿名方法（D3/D4，1b）
        var plans = new List<LambdaLiftPlan>();
        var capturedDeferred = 0;
        var asyncLambdasSeen = 0;

        if (options.LiftAsyncLambdas)
        {
            // 类型作用域：已占用成员名（防撞）+ (宿主成员名 → 序号)。
            // 键统一为同一棵原始语法树的类型节点引用，默认引用相等即可。
            var usedNamesByType = new Dictionary<TypeDeclarationSyntax, HashSet<string>>();
            var countersByType = new Dictionary<TypeDeclarationSyntax, Dictionary<string, int>>();

            foreach (var candidate in EnumerateCandidates(root))
            {
                asyncLambdasSeen += candidate is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax ? 1 : 0;

                var hostType = candidate.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (hostType == null)
                {
                    // 无类型归属（top-level statements 等）：v1 不提升。
                    if (candidate is AnonymousMethodExpressionSyntax or LambdaExpressionSyntax)
                    {
                        diagnostics.Add(DiagnosticAt(
                            AsyncNormalizeDiagnosticCode.CS2J1807, candidate,
                            "async lambda has no enclosing type declaration (e.g. top-level " +
                            "statements); the lightweight normalizer can only lift into a " +
                            "declared type."));
                    }
                    else if (candidate is LocalFunctionStatementSyntax)
                    {
                        diagnostics.Add(DiagnosticAt(
                            AsyncNormalizeDiagnosticCode.CS2J1804, candidate,
                            "residual async local function has no enclosing type declaration " +
                            "(e.g. top-level statements) and cannot be lifted by this pass."));
                    }
                    continue;
                }

                var usedNames = GetOrCreateUsedNames(usedNamesByType, hostType, model);
                var counters = GetOrCreateCounters(countersByType, hostType);
                var hostMemberName = GetHostMemberName(candidate, hostType);
                counters.TryGetValue(hostMemberName, out var index);
                counters[hostMemberName] = index + 1;

                // 基线优先：候选跨度内已有编译错误（表达式树内 async lambda 等）不抢诊断。
                if (BaselineContains(baselineAllErrors, model.SyntaxTree, candidate.Span))
                    continue;

                switch (candidate)
                {
                    case AnonymousMethodExpressionSyntax anonymous:
                        diagnostics.Add(DiagnosticAt(
                            AsyncNormalizeDiagnosticCode.CS2J1807, anonymous,
                            "async anonymous method ('async delegate { ... }') is not supported: " +
                            "rewrite it as an async lambda (or a named async method) so the " +
                            "normalizer can lift it."));
                        break;

                    case LambdaExpressionSyntax lambda:
                        AnalyzeLambda(
                            model, options, lambda, hostType, hostMemberName, index,
                            usedNames, diagnostics, plans, ref capturedDeferred);
                        break;

                    case LocalFunctionStatementSyntax localFunction:
                        AnalyzeLocalFunction(
                            model, localFunction, root, hostType, hostMemberName, index,
                            usedNames, diagnostics, plans);
                        break;
                }
            }

            PropagateInstanceRequirement(model, plans);
        }
        else
        {
            // kill-switch：仅保留 1a 的临时内部计数口径，不产生计划/skip 诊断。
            foreach (var node in root.DescendantNodes())
            {
                var hasAsync = node switch
                {
                    ParenthesizedLambdaExpressionSyntax lambda =>
                        lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
                    SimpleLambdaExpressionSyntax lambda =>
                        lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
                    AnonymousMethodExpressionSyntax anonymous =>
                        anonymous.Modifiers.Any(SyntaxKind.AsyncKeyword),
                    _ => false,
                };
                if (hasAsync) asyncLambdasSeen++;
            }
        }

        return new AsyncAnalysisResult(
            diagnostics,
            awaitSites,
            asyncMethodsSeen,
            asyncLambdasSeen,
            unsupportedAwaiters,
            asyncVoidCount,
            plans,
            capturedDeferred);
    }

    // ── D3：async lambda ──

    private static void AnalyzeLambda(
        SemanticModel model,
        AsyncNormalizeOptions options,
        LambdaExpressionSyntax lambda,
        TypeDeclarationSyntax hostType,
        string hostMemberName,
        int index,
        HashSet<string> usedNames,
        List<AsyncNormalizeDiagnostic> diagnostics,
        List<LambdaLiftPlan> plans,
        ref int capturedDeferred)
    {
        var convertedType = model.GetTypeInfo(lambda).ConvertedType as INamedTypeSymbol;
        var invoke = convertedType is { TypeKind: TypeKind.Delegate }
            ? convertedType.DelegateInvokeMethod
            : null;

        if (invoke == null)
        {
            if (IsExpressionTreeType(convertedType))
            {
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1807, lambda,
                    $"async lambda is converted to expression tree '{convertedType!.ToDisplayString()}'; " +
                    "async lambdas cannot be represented in an expression tree, so the lightweight " +
                    "normalizer leaves the node untouched."));
                return;
            }

            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1807, lambda,
                "target delegate type of the async lambda cannot be bound from the SemanticModel " +
                "(GetTypeInfo(lambda).ConvertedType); explicit 'new DelegateType(...)' lifting is " +
                "impossible without it."));
            return;
        }

        // ref/out/in 形参 / 指针形参的 async lambda 委托目标 v1 不支持。
        if (invoke.Parameters.Any(p => p.RefKind != RefKind.None)
            || invoke.ReturnsByRef || invoke.ReturnsByRefReadonly
            || invoke.Parameters.Any(p => p.Type is IPointerTypeSymbol))
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1807, lambda,
                $"async lambda target delegate '{convertedType!.ToDisplayString()}' has ref/in/out " +
                "parameters (or ref/pointer shapes); the lightweight normalizer does not lift them."));
            return;
        }

        var lambdaParameters = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter },
            ParenthesizedLambdaExpressionSyntax parenthesized =>
                parenthesized.ParameterList.Parameters.ToArray(),
            _ => Array.Empty<ParameterSyntax>(),
        };

        if (lambdaParameters.Length != invoke.Parameters.Length)
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1807, lambda,
                $"async lambda parameter count ({lambdaParameters.Length}) does not match its " +
                $"target delegate parameter count ({invoke.Parameters.Length}); params/optional " +
                "delegate shapes are not supported by the lightweight normalizer."));
            return;
        }

        var body = (SyntaxNode?)lambda switch
        {
            ParenthesizedLambdaExpressionSyntax p => (SyntaxNode?)p.Block ?? p.ExpressionBody,
            SimpleLambdaExpressionSyntax s => (SyntaxNode?)s.Block ?? s.ExpressionBody,
            _ => null,
        };
        if (body == null)
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1807, lambda,
                "async lambda has no body."));
            return;
        }

        var hostTypeSymbol = model.GetDeclaredSymbol(hostType) as INamedTypeSymbol;
        var lambdaMethodSymbol = model.GetSymbolInfo(lambda).Symbol as IMethodSymbol;
        var capture = AsyncLambdaCaptureAnalysis.Classify(
            model, body, lambdaMethodSymbol, hostTypeSymbol);

        var parameters = BuildLambdaParameters(lambdaParameters, invoke.Parameters);

        IReadOnlyList<LambdaCapturePlan> captures = Array.Empty<LambdaCapturePlan>();
        if (capture.Kind == AsyncLambdaCaptureKind.CapturedLocals)
        {
            var deferReason = ValidateCapturedLift(
                model, lambda, body, invoke, convertedType!, capture, parameters);
            if (deferReason != null)
            {
                AddCapturedDefer(lambda, capture, deferReason, diagnostics, ref capturedDeferred);
                return;
            }

            captures = BuildCapturePlans(model, body, capture);
        }

        var needsWrapper = invoke.ReturnsVoid;
        var returnTypeDisplay = needsWrapper
            ? TaskDisplayName
            : invoke.ReturnType.ToDisplayString(AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat);
        var expressionBodyNeedsReturn = !needsWrapper
            && invoke.ReturnType is INamedTypeSymbol { IsGenericType: true };
        var delegateDisplay = convertedType!.ToDisplayString(
            AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat);

        // R1b：类型参数携带交给统一解析器（S-08 约束闭包、S-03 三态）。
        // 捕获形参的类型同样可能引用外层方法/类型的类型参数，故并入签名类型集。
        var signatureTypes = BuildLambdaSignatureTypes(model, invoke, capture, body);
        var typeParameterPlan = SyntheticTypeParameterResolver.Resolve(
            model, lambda, body: body, signatureTypes: signatureTypes);

        if (typeParameterPlan.Diagnostics.Any(static d =>
                d.Code == SyntheticTypeParameterResolver.NotExpressibleCode))
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1807, lambda,
                "async lambda carries type parameter(s) whose constraints cannot be reproduced " +
                "safely by the lifted method: " +
                FirstNotExpressibleMessage(typeParameterPlan) +
                " The lambda is left unchanged (conservative fallback)."));
            return;
        }

        var (typeParameters, constraints) = MapTypeParameterPlan(typeParameterPlan);

        if (captures.Count > 0
            && captures.Any(c => typeParameters.Contains(c.Name, StringComparer.Ordinal)))
        {
            AddCapturedDefer(
                lambda, capture,
                $"capture name '{captures.First(c => typeParameters.Contains(c.Name, StringComparer.Ordinal)).Name}' " +
                "collides with a carried type parameter.",
                diagnostics, ref capturedDeferred);
            return;
        }

        var liftedName = AsyncNormalizeNaming.ResolveUniqueName(
            AsyncNormalizeNaming.GetLiftedMethodName(hostMemberName, index), usedNames);
        string? wrapperName = null;
        if (needsWrapper)
        {
            wrapperName = AsyncNormalizeNaming.ResolveUniqueName(
                AsyncNormalizeNaming.GetWrapperMethodName(liftedName), usedNames);
        }

        plans.Add(new LambdaLiftPlan(
            lambda,
            IsLocalFunction: false,
            hostType,
            hostMemberName,
            index,
            liftedName,
            wrapperName,
            IsStatic: capture.Kind != AsyncLambdaCaptureKind.ThisOnly,
            NeedsWrapper: needsWrapper,
            convertedType,
            delegateDisplay,
            parameters,
            captures,
            returnTypeDisplay,
            expressionBodyNeedsReturn,
            capture.Kind,
            typeParameters,
            typeParameterPlan.TypeArguments,
            constraints,
            CaptureLayout.Empty));
    }

    // ── D3(d)：安全捕获提升（保守等价性前置校验） ──

    /// <summary>
    /// 捕获型 async lambda 的提升前置条件。全部通过才返回 null；任何一项无法证明等价
    /// 都返回原因（调用方按 CS2J1802 defer 原样保留）。
    /// </summary>
    private static string? ValidateCapturedLift(
        SemanticModel model,
        LambdaExpressionSyntax lambda,
        SyntaxNode body,
        IMethodSymbol invoke,
        INamedTypeSymbol convertedType,
        AsyncLambdaCaptureAnalysis.CaptureResult capture,
        IReadOnlyList<LambdaParameterPlan> parameters)
    {
        if (invoke.ReturnsVoid)
            return "the target delegate returns void (async void has no lifted async equivalent).";

        if (!IsSupportedTaskReturnType(invoke.ReturnType))
        {
            return $"the target delegate returns '{invoke.ReturnType.ToDisplayString()}', which is " +
                "not Task/Task<T>/ValueTask/ValueTask<T>.";
        }

        if (IsExpressionTreeType(convertedType))
            return "the target delegate is an expression tree.";

        if (capture.HasThis)
            return "the lambda also captures 'this', which cannot be passed as a value parameter.";

        foreach (var symbol in capture.CapturedVariables)
        {
            switch (symbol)
            {
                case ILocalSymbol { RefKind: RefKind.None }:
                case IParameterSymbol { RefKind: RefKind.None }:
                case IRangeVariableSymbol:
                    break;
                default:
                    return $"capture '{symbol.Name}' is not a local/parameter/range variable, " +
                        "or it is passed by ref.";
            }

            var type = GetCapturedSymbolType(model, symbol, body);
            if (type == null)
                return $"the type of capture '{symbol.Name}' cannot be resolved.";
            if (!type.IsReferenceType)
            {
                return $"capture '{symbol.Name}' has non-reference type " +
                    $"'{type.ToDisplayString(AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat)}'; " +
                    "value types are not lifted.";
            }
        }

        var captureNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in capture.CapturedVariables)
        {
            if (!captureNames.Add(symbol.Name))
                return $"duplicate capture name '{symbol.Name}'.";
        }

        foreach (var parameter in parameters)
        {
            if (captureNames.Contains(parameter.Name))
                return $"capture name '{parameter.Name}' collides with a lambda parameter.";
        }

        var writtenInside = CollectWrittenInside(model, body, lambda);
        if (writtenInside == null)
            return "data flow analysis is unavailable for the lambda body.";
        if (capture.CapturedVariables.Any(writtenInside.Contains))
            return "a captured variable is written inside the lambda body.";

        var scope = FindEnclosingMemberBody(lambda);
        if (scope == null)
            return "the enclosing member body cannot be located.";

        var writtenInScope = CollectWrittenSymbols(scope, model);
        foreach (var symbol in capture.CapturedVariables)
        {
            if (writtenInScope.Contains(symbol))
            {
                return $"capture '{symbol.Name}' is assigned (or passed by ref/out) in the " +
                    "enclosing member; a value parameter would not observe later assignments.";
            }
        }

        return null;
    }

    private static void AddCapturedDefer(
        LambdaExpressionSyntax lambda,
        AsyncLambdaCaptureAnalysis.CaptureResult capture,
        string reason,
        List<AsyncNormalizeDiagnostic> diagnostics,
        ref int capturedDeferred)
    {
        capturedDeferred++;
        var names = string.Join(", ", capture.CapturedVariables.Select(static s => s.Name));
        diagnostics.Add(DiagnosticAt(
            AsyncNormalizeDiagnosticCode.CS2J1802, lambda,
            $"async lambda captures outer local/parameter/range variable(s) ({names}). This " +
            $"shape cannot be lifted equivalently: {reason} Manual fix: extract a named method " +
            "taking the captured values as explicit parameters, or pass the captured value " +
            "explicitly through the async lambda."));
    }

    private static IReadOnlyList<LambdaCapturePlan> BuildCapturePlans(
        SemanticModel model,
        SyntaxNode body,
        AsyncLambdaCaptureAnalysis.CaptureResult capture)
    {
        var entries = new List<(int Position, string Name, string TypeDisplay)>(
            capture.CapturedVariables.Count);

        foreach (var symbol in capture.CapturedVariables)
        {
            var type = GetCapturedSymbolType(model, symbol, body)!;
            var position = symbol.Locations
                .FirstOrDefault(location => location.IsInSource)?.SourceSpan.Start
                ?? int.MaxValue;
            entries.Add((
                position,
                symbol.Name,
                type.ToDisplayString(AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat)));
        }

        // 顺序稳定：源声明位置优先，同位（理论上不可达）按名字兜底。
        entries.Sort(static (left, right) =>
        {
            var byPosition = left.Position.CompareTo(right.Position);
            return byPosition != 0
                ? byPosition
                : string.CompareOrdinal(left.Name, right.Name);
        });

        return entries
            .Select(static entry => new LambdaCapturePlan(entry.Name, entry.TypeDisplay))
            .ToArray();
    }

    /// <summary>
    /// 捕获符号的类型：局部/形参直接取符号；foreach/查询 range 变量没有类型面，
    /// 从其在 lambda 体内的首个绑定标识符取 SemanticModel 类型。
    /// </summary>
    private static ITypeSymbol? GetCapturedSymbolType(
        SemanticModel model,
        ISymbol symbol,
        SyntaxNode body)
    {
        switch (symbol)
        {
            case ILocalSymbol local:
                return local.Type;
            case IParameterSymbol parameter:
                return parameter.Type;
        }

        foreach (var identifier in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(identifier).Symbol, symbol))
            {
                return model.GetTypeInfo(identifier).Type;
            }
        }

        return null;
    }

    private static HashSet<ISymbol>? CollectWrittenInside(
        SemanticModel model,
        SyntaxNode body,
        LambdaExpressionSyntax lambda)
    {
        DataFlowAnalysis? dataFlow;
        try
        {
            dataFlow = model.AnalyzeDataFlow(body);
        }
        catch (ArgumentException)
        {
            dataFlow = null;
        }

        if (dataFlow is not { Succeeded: true })
        {
            try
            {
                dataFlow = model.AnalyzeDataFlow(lambda);
            }
            catch (ArgumentException)
            {
                dataFlow = null;
            }
        }

        if (dataFlow is not { Succeeded: true })
            return null;

        return new HashSet<ISymbol>(dataFlow.WrittenInside, SymbolEqualityComparer.Default);
    }

    /// <summary>
    /// 外层成员体内对符号的全部写入：普通/复合赋值左值、前缀/后缀 ++/--、ref/out 实参、
    /// ref 表达式的目标。变量声明符的初始值不是 <c>AssignmentExpressionSyntax</c>，天然不计。
    /// </summary>
    private static HashSet<ISymbol> CollectWrittenSymbols(SyntaxNode scope, SemanticModel model)
    {
        var written = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        void AddTarget(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case IdentifierNameSyntax identifier:
                    var symbol = model.GetSymbolInfo(identifier).Symbol;
                    if (symbol != null)
                        written.Add(symbol);
                    break;

                case ParenthesizedExpressionSyntax parenthesized:
                    AddTarget(parenthesized.Expression);
                    break;

                case TupleExpressionSyntax tuple:
                    foreach (var argument in tuple.Arguments)
                        AddTarget(argument.Expression);
                    break;
            }
        }

        foreach (var node in scope.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assignment:
                    AddTarget(assignment.Left);
                    break;

                case PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.PreIncrementExpression)
                        || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    AddTarget(prefix.Operand);
                    break;

                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression)
                        || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    AddTarget(postfix.Operand);
                    break;

                case ArgumentSyntax argument
                    when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                        || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
                    AddTarget(argument.Expression);
                    break;

                case RefExpressionSyntax refExpression:
                    AddTarget(refExpression.Expression);
                    break;
            }
        }

        return written;
    }

    /// <summary>
    /// 捕获 lambda 的最近/最外层成员体（到类型声明为止，故嵌套局部函数内的写入也会覆盖）。
    /// </summary>
    private static SyntaxNode? FindEnclosingMemberBody(LambdaExpressionSyntax lambda)
    {
        SyntaxNode? body = null;
        foreach (var ancestor in lambda.Ancestors())
        {
            if (ancestor is TypeDeclarationSyntax)
                break;

            var candidate = ancestor switch
            {
                MethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
                ConstructorDeclarationSyntax constructor =>
                    (SyntaxNode?)constructor.Body ?? constructor.ExpressionBody,
                DestructorDeclarationSyntax destructor =>
                    (SyntaxNode?)destructor.Body ?? destructor.ExpressionBody,
                OperatorDeclarationSyntax @operator =>
                    (SyntaxNode?)@operator.Body ?? @operator.ExpressionBody,
                ConversionOperatorDeclarationSyntax conversion =>
                    (SyntaxNode?)conversion.Body ?? conversion.ExpressionBody,
                AccessorDeclarationSyntax accessor =>
                    (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody,
                LocalFunctionStatementSyntax localFunction =>
                    (SyntaxNode?)localFunction.Body ?? localFunction.ExpressionBody,
                PropertyDeclarationSyntax property => property.ExpressionBody,
                IndexerDeclarationSyntax indexer => indexer.ExpressionBody,
                _ => null,
            };
            if (candidate != null)
                body = candidate;
        }

        return body;
    }

    private static bool IsSupportedTaskReturnType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;
        var definition = named.OriginalDefinition;
        if (definition.ContainingNamespace?.ToDisplayString() != "System.Threading.Tasks")
            return false;
        return definition is { Name: "Task" or "ValueTask", Arity: <= 1 };
    }

    private static bool IsExpressionTreeType(INamedTypeSymbol? type)
    {
        if (type?.OriginalDefinition is not { Name: "Expression", Arity: 1 } definition)
            return false;
        return definition.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions";
    }

    private static IReadOnlyList<LambdaParameterPlan> BuildLambdaParameters(
        IReadOnlyList<ParameterSyntax> lambdaParameters,
        IReadOnlyList<IParameterSymbol> delegateParameters)
    {
        var result = new List<LambdaParameterPlan>(lambdaParameters.Count);
        var usedParamNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lambdaParameters.Count; i++)
        {
            var syntax = lambdaParameters[i];
            var symbol = delegateParameters[i];

            var name = syntax.Identifier.ValueText;
            if (string.IsNullOrEmpty(name) || name == "_" || !usedParamNames.Add(name))
                name = "_cs2jArg_" + i;

            var typeDisplay = symbol.Type.ToDisplayString(
                AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat);
            result.Add(new LambdaParameterPlan(name, typeDisplay, symbol.RefKind));
        }
        return result;
    }

    // ── D4：残余 async 局部函数 ──

    private static void AnalyzeLocalFunction(
        SemanticModel model,
        LocalFunctionStatementSyntax localFunction,
        CompilationUnitSyntax root,
        TypeDeclarationSyntax hostType,
        string hostMemberName,
        int index,
        HashSet<string> usedNames,
        List<AsyncNormalizeDiagnostic> diagnostics,
        List<LambdaLiftPlan> plans)
    {
        if (model.GetDeclaredSymbol(localFunction) is not IMethodSymbol symbol)
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1804, localFunction,
                "residual async local function cannot be bound by the SemanticModel."));
            return;
        }

        // async void 局部函数归 CS2J1803（D6），不重复诊断/不提升。
        if (symbol.ReturnsVoid)
            return;

        if (localFunction.ReturnType == null)
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1804, localFunction,
                $"residual async local function '{symbol.Name}' has no explicit return type; " +
                "the lightweight normalizer requires one to lift it."));
            return;
        }

        SyntaxNode body;
        if (localFunction.Body != null)
            body = localFunction.Body;
        else if (localFunction.ExpressionBody != null)
            body = localFunction.ExpressionBody;
        else
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1804, localFunction,
                $"residual async local function '{symbol.Name}' has no body."));
            return;
        }

        var hostTypeSymbol = model.GetDeclaredSymbol(hostType) as INamedTypeSymbol;
        var capture = AsyncLambdaCaptureAnalysis.Classify(model, body, symbol, hostTypeSymbol);

        if (capture.Kind == AsyncLambdaCaptureKind.CapturedLocals)
        {
            var names = string.Join(", ", capture.CapturedVariables.Select(static s => s.Name));
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1804, localFunction,
                $"residual async local function '{symbol.Name}' captures outer local/parameter/" +
                $"range variable(s) ({names}) and was not lifted by local-function-lift (E02); " +
                "this pass can only lift zero-capture/this-only residual local functions. " +
                "Manual fix: extract a named method taking the captured values as parameters."));
            return;
        }

        // 被外层 lambda/匿名方法以方法组或调用引用的局部函数 v1 不提升
        //（提升后委托捕获/静态实例关系需要 display class 配合）。
        if (IsReferencedInsideAnonymousFunction(model, root, localFunction, symbol))
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1804, localFunction,
                $"residual async local function '{symbol.Name}' is referenced inside a lambda/" +
                "anonymous method; lifting it to a type-level method before closure-lift (C45) " +
                "would change delegate-capture semantics."));
            return;
        }

        var parameters = new List<LambdaParameterPlan>();
        foreach (var parameter in localFunction.ParameterList.Parameters)
        {
            var parameterSymbol = model.GetDeclaredSymbol(parameter) as IParameterSymbol;
            var refKind = parameterSymbol?.RefKind
                ?? (parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)) ? RefKind.Ref
                    : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)) ? RefKind.Out
                    : parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)) ? RefKind.In
                    : RefKind.None);
            var typeDisplay = parameter.Type != null
                ? parameter.Type.ToString()
                : "global::System.Object";
            var name = parameter.Identifier.ValueText;
            if (string.IsNullOrEmpty(name))
                name = "_cs2jArg";
            parameters.Add(new LambdaParameterPlan(name, typeDisplay, refKind));
        }

        var returnTypeDisplay = localFunction.ReturnType.ToString();
        var expressionBodyNeedsReturn = symbol.ReturnType is INamedTypeSymbol { IsGenericType: true };

        // R1b：own 形参 + 外层方法/局部函数闭包交给统一解析器（宿主类级形参不重复声明）。
        var signatureTypes = new List<ITypeSymbol?>(localFunction.ParameterList.Parameters.Count + 1);
        foreach (var parameter in localFunction.ParameterList.Parameters)
            signatureTypes.Add((model.GetDeclaredSymbol(parameter) as IParameterSymbol)?.Type);
        signatureTypes.Add(symbol.ReturnType);

        var typeParameterPlan = SyntheticTypeParameterResolver.Resolve(
            model, localFunction,
            ownTypeParameterList: localFunction.TypeParameterList,
            ownConstraintClauses: localFunction.ConstraintClauses,
            body: body,
            signatureTypes: signatureTypes);

        if (typeParameterPlan.Diagnostics.Any(static d =>
                d.Code == SyntheticTypeParameterResolver.NotExpressibleCode))
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1804, localFunction,
                $"residual async local function '{symbol.Name}' carries type parameter(s) whose " +
                "constraints cannot be reproduced safely by the lifted method: " +
                FirstNotExpressibleMessage(typeParameterPlan) +
                " It is left unchanged (conservative fallback)."));
            return;
        }

        var (typeParameters, constraints) = MapTypeParameterPlan(typeParameterPlan);

        var liftedName = AsyncNormalizeNaming.ResolveUniqueName(
            AsyncNormalizeNaming.GetLiftedMethodName(hostMemberName, index), usedNames);

        plans.Add(new LambdaLiftPlan(
            localFunction,
            IsLocalFunction: true,
            hostType,
            hostMemberName,
            index,
            liftedName,
            WrapperName: null,
            IsStatic: capture.Kind == AsyncLambdaCaptureKind.ZeroCapture,
            NeedsWrapper: false,
            DelegateType: null,
            DelegateTypeDisplay: null,
            parameters,
            Captures: Array.Empty<LambdaCapturePlan>(),
            returnTypeDisplay,
            expressionBodyNeedsReturn,
            capture.Kind,
            typeParameters,
            typeParameterPlan.TypeArguments,
            constraints,
            CaptureLayout.Empty));
    }

    /// <summary>
    /// 局部函数在自身声明跨度之外、被 lambda/匿名方法内部以调用或方法组引用时返回 true。
    /// 绑定经 SemanticModel（含构造泛型方法的 OriginalDefinition 比较），防误改同名标识符。
    /// </summary>
    private static bool IsReferencedInsideAnonymousFunction(
        SemanticModel model,
        CompilationUnitSyntax root,
        LocalFunctionStatementSyntax localFunction,
        IMethodSymbol localSymbol)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (localFunction.Span.Contains(invocation.Span))
                continue;
            if (!IsInsideAnonymousFunction(invocation))
                continue;
            if (BindsToLocalFunction(model.GetSymbolInfo(invocation).Symbol, localSymbol))
                return true;
        }

        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (localFunction.Span.Contains(identifier.Span))
                continue;
            if (identifier.Parent is InvocationExpressionSyntax invocation
                && invocation.Expression == identifier)
            {
                continue; // 调用路径已在上面覆盖
            }
            if (!IsInsideAnonymousFunction(identifier))
                continue;
            if (BindsToLocalFunction(model.GetSymbolInfo(identifier).Symbol, localSymbol))
                return true;
        }

        return false;
    }

    private static bool IsInsideAnonymousFunction(SyntaxNode node)
        => node.Ancestors()
            .Any(a => a is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax);

    private static bool BindsToLocalFunction(ISymbol? resolved, IMethodSymbol localSymbol)
    {
        if (resolved is not IMethodSymbol method)
            return false;
        return SymbolEqualityComparer.Default.Equals(method, localSymbol)
            || SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, localSymbol);
    }

    /// <summary>
    /// 固定点传播：零捕获本可 static 的提升方法，若其体内引用了任一最终必须为实例方法的
    /// 姊妹提升方法（零捕获 DFA 看不到跨方法引用），则改为实例方法，保证输出可编译。
    /// </summary>
    private static void PropagateInstanceRequirement(
        SemanticModel model,
        List<LambdaLiftPlan> plans)
    {
        var bySymbol = new Dictionary<IMethodSymbol, LambdaLiftPlan>(SymbolEqualityComparer.Default);
        foreach (var plan in plans.Where(plan => plan.IsLocalFunction))
        {
            if (model.GetDeclaredSymbol((LocalFunctionStatementSyntax)plan.Node) is IMethodSymbol symbol)
                bySymbol[symbol] = plan;
        }

        var instanceRequired = new HashSet<LambdaLiftPlan>(plans.Where(p => !p.IsStatic));

        var bodyRoot = new Func<LambdaLiftPlan, SyntaxNode?>(p => p.Node switch
        {
            ParenthesizedLambdaExpressionSyntax lambda => (SyntaxNode?)lambda.Block ?? lambda.ExpressionBody,
            SimpleLambdaExpressionSyntax simple => (SyntaxNode?)simple.Block ?? simple.ExpressionBody,
            LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
            _ => null,
        });

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var plan in plans)
            {
                if (instanceRequired.Contains(plan))
                    continue;

                var body = bodyRoot(plan);
                if (body == null)
                    continue;

                foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var resolved = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (resolved == null)
                        continue;
                    if (bySymbol.TryGetValue(resolved, out var referenced)
                        || bySymbol.TryGetValue(resolved.OriginalDefinition, out referenced))
                    {
                        if (instanceRequired.Contains(referenced))
                        {
                            instanceRequired.Add(plan);
                            changed = true;
                            break;
                        }
                    }
                }
            }
        }

        if (instanceRequired.Count > 0)
        {
            for (var i = 0; i < plans.Count; i++)
            {
                if (instanceRequired.Contains(plans[i]) && plans[i].IsStatic)
                    plans[i] = plans[i] with { IsStatic = false };
            }
        }
    }

    // ── 泛型携带（R1b：统一解析器 SyntheticTypeParameterResolver） ──

    /// <summary>
    /// S-08 约束闭包 + 三态由统一解析器给出；这里只做 LambdaLiftPlan 的形状映射。
    /// </summary>
    private static (IReadOnlyList<string> TypeParameters,
                    IReadOnlyList<TypeParameterConstraintClauseSyntax> Constraints)
        MapTypeParameterPlan(SyntheticTypeParameterPlan plan)
    {
        var typeParameters = new List<string>(plan.Parameters.Count);
        var constraints = new List<TypeParameterConstraintClauseSyntax>();
        foreach (var parameter in plan.Parameters)
        {
            // NotExpressible 已由调用方保守回退；防御性跳过，绝不静默丢弃后继续发射。
            if (parameter.State == SyntheticTypeParameterState.NotExpressible)
                continue;

            typeParameters.Add(parameter.RenderName);
            if (parameter.Constraint is { } clause)
                constraints.Add(RenameConstraintClause(clause, parameter.RenderName));
        }

        return (typeParameters, constraints);
    }

    private static TypeParameterConstraintClauseSyntax RenameConstraintClause(
        TypeParameterConstraintClauseSyntax clause,
        string renderName)
        => string.Equals(clause.Name.Identifier.ValueText, renderName, StringComparison.Ordinal)
            ? clause
            : clause.WithName(
                SyntaxFactory.IdentifierName(renderName).WithTriviaFrom(clause.Name));

    private static string FirstNotExpressibleMessage(SyntheticTypeParameterPlan plan)
        => plan.Diagnostics.FirstOrDefault(static d =>
               d.Code == SyntheticTypeParameterResolver.NotExpressibleCode)?.Message
           ?? "an unknown constraint";

    /// <summary>
    /// Lambda 的签名类型集：委托 ConvertedType 的形参/返回类型；有安全捕获时追加捕获
    /// 符号的类型（其类型也可能引用外层方法类型参数）。零捕获返回 null，让解析器
    /// 自行从 ConvertedType 派生。
    /// </summary>
    private static IReadOnlyList<ITypeSymbol?>? BuildLambdaSignatureTypes(
        SemanticModel model,
        IMethodSymbol invoke,
        AsyncLambdaCaptureAnalysis.CaptureResult capture,
        SyntaxNode body)
    {
        if (capture.Kind != AsyncLambdaCaptureKind.CapturedLocals
            || capture.CapturedVariables.Count == 0)
        {
            return null;
        }

        var types = new List<ITypeSymbol?>(invoke.Parameters.Length + 1 + capture.CapturedVariables.Count);
        foreach (var parameter in invoke.Parameters)
            types.Add(parameter.Type);
        types.Add(invoke.ReturnType);
        foreach (var symbol in capture.CapturedVariables)
            types.Add(GetCapturedSymbolType(model, symbol, body));
        return types;
    }

    // ── 候选枚举 / 宿主解析 ──

    /// <summary>
    /// 后序枚举全部 async lambda / async 匿名方法 / async 局部函数：
    /// 嵌套节点内层先于外层（内层先编号/先外提），并列节点按源 span 序，跨次运行确定。
    /// </summary>
    private static IEnumerable<SyntaxNode> EnumerateCandidates(SyntaxNode root)
    {
        IEnumerable<SyntaxNode> Walk(SyntaxNode node)
        {
            foreach (var child in node.ChildNodes())
            {
                foreach (var inner in Walk(child))
                    yield return inner;
            }

            var isCandidate = node switch
            {
                ParenthesizedLambdaExpressionSyntax lambda =>
                    lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
                SimpleLambdaExpressionSyntax lambda =>
                    lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
                AnonymousMethodExpressionSyntax anonymous =>
                    anonymous.Modifiers.Any(SyntaxKind.AsyncKeyword),
                LocalFunctionStatementSyntax local =>
                    local.Modifiers.Any(SyntaxKind.AsyncKeyword),
                _ => false,
            };
            if (isCandidate)
                yield return node;
        }

        return Walk(root);
    }

    private static HashSet<string> GetOrCreateUsedNames(
        Dictionary<TypeDeclarationSyntax, HashSet<string>> cache,
        TypeDeclarationSyntax hostType,
        SemanticModel model)
    {
        if (cache.TryGetValue(hostType, out var existing))
            return existing;

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in hostType.Members)
        {
            var name = member switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                EventDeclarationSyntax @event => @event.Identifier.ValueText,
                IndexerDeclarationSyntax => "This", // 不与 Cs2jAsync_ 前缀冲突，占位即可
                FieldDeclarationSyntax field => field.Declaration.Variables
                    .FirstOrDefault()?.Identifier.ValueText,
                TypeDeclarationSyntax nested => nested.Identifier.ValueText,
                DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
                ConstructorDeclarationSyntax => hostType.Identifier.ValueText,
                _ => null,
            };
            if (!string.IsNullOrEmpty(name))
                used.Add(name);
        }

        cache[hostType] = used;
        return used;
    }

    private static Dictionary<string, int> GetOrCreateCounters(
        Dictionary<TypeDeclarationSyntax, Dictionary<string, int>> cache,
        TypeDeclarationSyntax hostType)
    {
        if (!cache.TryGetValue(hostType, out var counters))
        {
            counters = new Dictionary<string, int>(StringComparer.Ordinal);
            cache[hostType] = counters;
        }
        return counters;
    }

    private static string GetHostMemberName(SyntaxNode candidate, TypeDeclarationSyntax hostType)
    {
        foreach (var ancestor in candidate.Ancestors())
        {
            switch (ancestor)
            {
                case LocalFunctionStatementSyntax local:
                    return SanitizeMemberName(local.Identifier.ValueText);
                case MethodDeclarationSyntax method:
                    return SanitizeMemberName(method.Identifier.ValueText);
                case ConstructorDeclarationSyntax:
                    return SanitizeMemberName(hostType.Identifier.ValueText);
                case PropertyDeclarationSyntax property:
                    return SanitizeMemberName(property.Identifier.ValueText);
                case EventDeclarationSyntax @event:
                    return SanitizeMemberName(@event.Identifier.ValueText);
                case IndexerDeclarationSyntax:
                    return "Item";
            }
            if (ancestor == hostType)
                break;
        }

        return SanitizeMemberName(hostType.Identifier.ValueText);
    }

    private static string SanitizeMemberName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "Local";
        var chars = name.Select(static c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        if (!char.IsLetter(chars[0]) && chars[0] != '_')
            chars[0] = '_';
        return new string(chars);
    }

    // ── 基线快照 ──

    private static List<(SyntaxTree Tree, TextSpan Span)> CollectBaselineLockErrors(SemanticModel model)
        => CollectTreeErrorSpans(model, "CS1996");

    /// <summary>
    /// 不安全上下文 await 的基线错误 CS4004 快照（CS2J1809 基线优先用）。
    /// 注意：CS4007/CS9217 曾在此列，探针证明 Roslyn 5 已不再发出（C# 13 合法化），移除。
    /// </summary>
    private static List<(SyntaxTree Tree, TextSpan Span)> CollectBaselineUnsafeAwaitErrors(SemanticModel model)
        => CollectTreeErrorSpans(model, "CS4004");

    private static List<(SyntaxTree Tree, TextSpan Span)> CollectBaselineErrors(SemanticModel model)
        => CollectTreeErrorSpans(model, null);

    /// <summary>
    /// 当前语法树内的 Error 级诊断 (树, 跨度) 快照。基线集合只在
    /// <see cref="BaselineContains"/> 里以 <c>model.SyntaxTree</c> 查询，因此只需本树
    /// 的诊断；直接对 <see cref="SemanticModel.GetDiagnostics"/> 求值，避免对整棵编译
    /// （全部源文件）反复全量绑定。
    /// </summary>
    internal static List<(SyntaxTree Tree, TextSpan Span)> CollectTreeErrorSpans(
        SemanticModel model, string? code)
    {
        var result = new List<(SyntaxTree, TextSpan)>();
        foreach (var diagnostic in model.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error) continue;
            if (code != null && diagnostic.Descriptor.Id != code) continue;
            if (diagnostic.Location.SourceTree is not { } tree) continue;
            result.Add((tree, diagnostic.Location.SourceSpan));
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

/// <summary>
/// Analyzer 单文件分析结果。<see cref="AsyncLambdasSeen"/> 为内部计数（不进入 Statistics）；
/// 1b 起 <see cref="Plans"/> 携带全部可提升计划，<see cref="CapturedAsyncLambdasDeferred"/>
/// 为 D3(d) defer 站点数。
/// </summary>
internal sealed record AsyncAnalysisResult(
    IReadOnlyList<AsyncNormalizeDiagnostic> Diagnostics,
    int AwaitSites,
    int AsyncMethodsSeen,
    int AsyncLambdasSeen,
    int UnsupportedAwaiters,
    int AsyncVoidCount,
    IReadOnlyList<LambdaLiftPlan> Plans,
    int CapturedAsyncLambdasDeferred);
