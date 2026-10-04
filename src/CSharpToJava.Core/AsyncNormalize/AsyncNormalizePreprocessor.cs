using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using CSharpToJava.Core.AsyncNormalize.Await;

namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// C18 async-normalize 公共入口。形态对齐 IteratorLoweringPreprocessor：
/// options 兜底 → mode 门控 → dirty check → analyze → (ConfigureAwait(bool) rewrite)
/// → Formatter.Format → 重解析 residual 闸门。轻量版不改写 await 本身。
/// </summary>
public sealed class AsyncNormalizePreprocessor
{
    public AsyncNormalizeFileResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        AsyncNormalizeOptions? options = null)
    {
        options ??= new AsyncNormalizeOptions();

        if (options.Mode == AsyncNormalizeMode.Heavyweight)
        {
            throw new NotSupportedException(
                "AsyncNormalizeMode.Heavyweight (state machine elimination) is a reserved value " +
                "and is not implemented; use AsyncNormalizeMode.Lightweight.");
        }

        var stats = new AsyncNormalizeStatistics { FilesScanned = 1 };
        var diagnostics = new List<AsyncNormalizeDiagnostic>();

        if (semanticModel == null)
        {
            stats.FilesSkippedClean = 1;
            return new AsyncNormalizeFileResult(sourceCode, false, diagnostics, stats);
        }

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        // Dirty check：无任何异步面 → 字节级原样复制。
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new AsyncNormalizeFileResult(sourceCode, false, diagnostics, stats);
        }

        // Analyze（非严格：1801/1803/1805/1808 降 Warning，节点原样保留）。
        var analysis = AsyncNormalizeAnalyzer.Analyze(semanticModel, options);
        foreach (var diagnostic in analysis.Diagnostics)
        {
            var severity = diagnostic.Severity;
            if (!options.Strict && severity == AsyncNormalizeSeverity.Error)
                severity = AsyncNormalizeSeverity.Warning;
            diagnostics.Add(diagnostic with { Severity = severity });
        }

        // Rewrite：1a 唯一真实改写是 ConfigureAwait(bool) 剥离（非严格也照剥，属正常归一）。
        var strippedCount = 0;
        var newRoot = root;
        if (options.StripConfigureAwait)
        {
            var rewriter = new AwaitShapeRewriter(semanticModel);
            newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
            strippedCount = rewriter.ConfigureAwaitStripped;
            diagnostics.AddRange(rewriter.Diagnostics);
        }

        var changed = strippedCount > 0;
        var outputCode = sourceCode;

        if (changed)
        {
            using var workspace = new AdhocWorkspace();
            outputCode = Formatter.Format(newRoot, workspace).ToFullString();
            stats.FilesTransformed = 1;
        }
        else
        {
            // 仅诊断无改写 → 原样返回。
            stats.FilesDirtyNoRewrite = 1;
        }

        // Statistics 桶累加。
        stats.AsyncMethodsSeen = analysis.AsyncMethodsSeen;
        stats.AwaitSites = analysis.AwaitSites;
        stats.ConfigureAwaitStripped = strippedCount;
        stats.AsyncVoidRejected = analysis.AsyncVoidCount;
        stats.UnsupportedAwaiters = analysis.UnsupportedAwaiters;
        stats.SkippedUnsupported = analysis.Diagnostics.Count;

        // Residual 闸门：输出树残余 ConfigureAwait(bool)（未被 CS2J1808 覆盖的形态）
        // + 未豁免 async void。重绑编译做符号判定，自定义同名扩展不计违规。
        var residualCount = CountResidual(outputCode, semanticModel);
        stats.ResidualCount = residualCount;
        if (residualCount > 0 && options.Strict)
        {
            diagnostics.Add(new AsyncNormalizeDiagnostic(
                AsyncNormalizeSeverity.Error,
                AsyncNormalizeDiagnosticCode.CS2J1810,
                $"{residualCount} residual async construct(s) remain after async normalization " +
                "(ConfigureAwait call(s) or un-exempted async void member(s))."));
        }

        return new AsyncNormalizeFileResult(outputCode, changed, diagnostics, stats);
    }

    private static bool IsDirty(CompilationUnitSyntax root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case AwaitExpressionSyntax:
                    return true;
                case MethodDeclarationSyntax method
                    when method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)):
                    return true;
                case LocalFunctionStatementSyntax local
                    when local.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)):
                    return true;
                case ParenthesizedLambdaExpressionSyntax lambda
                    when lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword):
                    return true;
                case SimpleLambdaExpressionSyntax lambda
                    when lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword):
                    return true;
                case AnonymousMethodExpressionSyntax anonymous
                    when anonymous.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)):
                    return true;
                case IdentifierNameSyntax identifier
                    when identifier.Identifier.Text == "ConfigureAwait":
                    return true;
            }
        }
        return false;
    }

    private static int CountResidual(string outputCode, SemanticModel originalModel)
    {
        var parseOptions = originalModel.SyntaxTree.Options as CSharpParseOptions;
        var outputTree = CSharpSyntaxTree.ParseText(outputCode, parseOptions);
        var outputRoot = (CompilationUnitSyntax)outputTree.GetRoot();

        SemanticModel? outputModel = null;
        if (originalModel.Compilation is CSharpCompilation compilation)
        {
            var rebound = compilation.ReplaceSyntaxTree(originalModel.SyntaxTree, outputTree);
            outputModel = rebound.GetSemanticModel(outputTree);
        }

        var residual = 0;

        if (outputModel != null)
        {
            foreach (var invocation in outputRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // bool 形态未被剥离 = 违规残余；options 形态已由 CS2J1808 覆盖（白名单）；
                // 非 Task receiver 的同名扩展不绑定 BCL 方法，不计违规。
                if (AsyncShapeClassifier.IsConfigureAwaitBool(outputModel, invocation, out var isOptionsForm)
                    && !isOptionsForm)
                {
                    residual++;
                }
            }

            foreach (var method in outputRoot.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (outputModel.GetDeclaredSymbol(method) is IMethodSymbol symbol
                    && symbol.IsAsync
                    && symbol.ReturnsVoid
                    && !method.HasAnnotations(AsyncNormalizeAnnotations.AnnotationKind))
                {
                    residual++;
                }
            }

            foreach (var local in outputRoot.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {
                if (outputModel.GetDeclaredSymbol(local) is IMethodSymbol symbol
                    && symbol.IsAsync
                    && symbol.ReturnsVoid
                    && !local.HasAnnotations(AsyncNormalizeAnnotations.AnnotationKind))
                {
                    residual++;
                }
            }
        }
        else
        {
            // 兜底：无法重绑编译（非 CSharpCompilation 宿主）时退化为纯语法计数。
            foreach (var invocation in outputRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess
                    && memberAccess.Name.Identifier.Text == "ConfigureAwait")
                {
                    residual++;
                }
            }
        }

        return residual;
    }
}

public sealed record AsyncNormalizeFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<AsyncNormalizeDiagnostic> Diagnostics,
    AsyncNormalizeStatistics Statistics);
