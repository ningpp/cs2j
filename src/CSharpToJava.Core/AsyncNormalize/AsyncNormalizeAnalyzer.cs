using CSharpToJava.Core.AsyncNormalize.Lambda;
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

        // ③ ConfigureAwait 调用：options 形态 → CS2J1808；bool 形态交 AwaitShapeRewriter 剥离
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!AsyncShapeClassifier.IsConfigureAwaitBool(model, invocation, out var isOptionsForm))
                continue;
            if (isOptionsForm)
            {
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1808, invocation,
                    "ConfigureAwait(ConfigureAwaitOptions) cannot be normalized: the options form " +
                    "(e.g. SuppressThrowing) has no Java-side equivalent."));
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

        if (capture.Kind == AsyncLambdaCaptureKind.CapturedLocals)
        {
            capturedDeferred++;
            var names = string.Join(", ", capture.CapturedVariables.Select(static s => s.Name));
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1802, lambda,
                $"async lambda captures outer local/parameter/range variable(s) ({names}). C# " +
                "closures capture variables (not values); until closure-lift (C45) provides the " +
                "display-class layout this cannot be lifted equivalently. Manual fix: extract a " +
                "named method taking the captured values as explicit parameters, or wait for " +
                "closure-lift."));
            return;
        }

        var parameters = BuildLambdaParameters(lambdaParameters, invoke.Parameters);
        var needsWrapper = invoke.ReturnsVoid;
        var returnTypeDisplay = needsWrapper
            ? TaskDisplayName
            : invoke.ReturnType.ToDisplayString(AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat);
        var expressionBodyNeedsReturn = !needsWrapper
            && invoke.ReturnType is INamedTypeSymbol { IsGenericType: true };
        var delegateDisplay = convertedType!.ToDisplayString(
            AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat);

        var (typeParameters, constraints) = ResolveGenericCarry(parameters, returnTypeDisplay, body);

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
            IsStatic: capture.Kind == AsyncLambdaCaptureKind.ZeroCapture,
            NeedsWrapper: needsWrapper,
            convertedType,
            delegateDisplay,
            parameters,
            returnTypeDisplay,
            expressionBodyNeedsReturn,
            capture.Kind,
            typeParameters,
            constraints,
            CaptureLayout.Empty));
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
        var (typeParameters, constraints) = ResolveGenericCarry(
            parameters, returnTypeDisplay, body,
            ownTypeParameterList: localFunction.TypeParameterList,
            ownConstraintClauses: localFunction.ConstraintClauses);

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
            returnTypeDisplay,
            expressionBodyNeedsReturn,
            capture.Kind,
            typeParameters,
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

    // ── 泛型携带（设计 §4.2：外层方法类型参数前置） ──

    private static (IReadOnlyList<string> TypeParameters, IReadOnlyList<TypeParameterConstraintClauseSyntax> Constraints)
        ResolveGenericCarry(
            IReadOnlyList<LambdaParameterPlan> parameters,
            string returnTypeDisplay,
            SyntaxNode body,
            TypeParameterListSyntax? ownTypeParameterList = null,
            SyntaxList<TypeParameterConstraintClauseSyntax> ownConstraintClauses = default)
    {
        var typeParameters = new List<string>();
        var constraints = new List<TypeParameterConstraintClauseSyntax>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);

        if (ownTypeParameterList != null)
        {
            foreach (var tp in ownTypeParameterList.Parameters)
            {
                if (seenTypes.Add(tp.Identifier.ValueText))
                    typeParameters.Add(tp.Identifier.ValueText);
            }
            foreach (var clause in ownConstraintClauses)
                constraints.Add(clause);
        }

        // 体节点 → 类型声明之间的方法/局部函数祖先，按外层→内层。
        // 对局部函数候选，其自身的类型参数已由 ownTypeParameterList 登记，seenTypes 去重兜底。
        var ancestors = body.Ancestors()
            .TakeWhile(static a => a is not TypeDeclarationSyntax)
            .Where(static a => a is MethodDeclarationSyntax or LocalFunctionStatementSyntax)
            .Reverse()
            .ToList();

        // 体内/签名实际使用到的标识符名集合。
        var usedIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            usedIdentifiers.Add(id.Identifier.ValueText);
        foreach (var p in parameters)
        {
            foreach (var id in SyntaxFactory.ParseTypeName(p.TypeDisplay).DescendantNodesAndSelf()
                         .OfType<IdentifierNameSyntax>())
            {
                usedIdentifiers.Add(id.Identifier.ValueText);
            }
        }
        foreach (var id in SyntaxFactory.ParseTypeName(returnTypeDisplay).DescendantNodesAndSelf()
                     .OfType<IdentifierNameSyntax>())
        {
            usedIdentifiers.Add(id.Identifier.ValueText);
        }

        foreach (var ancestor in ancestors)
        {
            var ancestorTypeParameters = ancestor is MethodDeclarationSyntax m
                ? m.TypeParameterList
                : ((LocalFunctionStatementSyntax)ancestor).TypeParameterList;
            if (ancestorTypeParameters == null)
                continue;

            var ancestorConstraints = ancestor is MethodDeclarationSyntax m2
                ? m2.ConstraintClauses
                : ((LocalFunctionStatementSyntax)ancestor).ConstraintClauses;

            foreach (var tp in ancestorTypeParameters.Parameters)
            {
                var tpName = tp.Identifier.ValueText;
                if (!seenTypes.Add(tpName))
                    continue;
                if (!usedIdentifiers.Contains(tpName))
                    continue;

                typeParameters.Add(tpName);
                var clause = ancestorConstraints.FirstOrDefault(c =>
                    c.Name.Identifier.ValueText == tpName);
                if (clause != null)
                    constraints.Add(clause);
            }
        }

        return (typeParameters, constraints);
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
    {
        var result = new List<(SyntaxTree, TextSpan)>();
        foreach (var diagnostic in model.Compilation.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error) continue;
            if (diagnostic.Descriptor.Id != "CS1996") continue;
            if (diagnostic.Location.SourceTree is not { } sourceTree) continue;
            result.Add((sourceTree, diagnostic.Location.SourceSpan));
        }
        return result;
    }

    /// <summary>
    /// 不安全上下文 await 的基线错误 CS4004 快照（CS2J1809 基线优先用）。
    /// 注意：CS4007/CS9217 曾在此列，探针证明 Roslyn 5 已不再发出（C# 13 合法化），移除。
    /// </summary>
    private static List<(SyntaxTree Tree, TextSpan Span)> CollectBaselineUnsafeAwaitErrors(SemanticModel model)
    {
        var result = new List<(SyntaxTree, TextSpan)>();
        foreach (var diagnostic in model.Compilation.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error) continue;
            if (diagnostic.Descriptor.Id != "CS4004") continue;
            if (diagnostic.Location.SourceTree is not { } sourceTree) continue;
            result.Add((sourceTree, diagnostic.Location.SourceSpan));
        }
        return result;
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
