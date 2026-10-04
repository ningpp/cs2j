using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// 整树发现与合法性分析（设计 §2 D5/D6/D10/D15、§4.1、§6）。
/// 语法粗筛 + SemanticModel 符号双判；基线优先——Roslyn 已报的 CS1996 不二次报错。
/// skip 集在任何改写前确定（静态、幂等）。诊断一律以 Error 级发射，非严格降级由入口统一处理。
/// </summary>
internal static class AsyncNormalizeAnalyzer
{
    public static AsyncAnalysisResult Analyze(SemanticModel model, AsyncNormalizeOptions options)
    {
        var diagnostics = new List<AsyncNormalizeDiagnostic>();
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

        var baselineLockErrors = CollectBaselineLockErrors(model);

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

        // ⑥ async lambda：1a 只做临时内部计数（不报错、不改写；提升在 1b）
        var asyncLambdasSeen = 0;
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

        return new AsyncAnalysisResult(
            diagnostics,
            awaitSites,
            asyncMethodsSeen,
            asyncLambdasSeen,
            unsupportedAwaiters,
            asyncVoidCount);
    }

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
/// Analyzer 单文件分析结果。<see cref="AsyncLambdasSeen"/> 为 1a 临时内部计数，
/// 不进入 Statistics（lambda 提升桶在 1b 建立）。
/// </summary>
internal sealed record AsyncAnalysisResult(
    IReadOnlyList<AsyncNormalizeDiagnostic> Diagnostics,
    int AwaitSites,
    int AsyncMethodsSeen,
    int AsyncLambdasSeen,
    int UnsupportedAwaiters,
    int AsyncVoidCount);
