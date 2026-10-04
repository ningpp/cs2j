using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using CSharpToJava.Core.AsyncNormalize.Await;
using CSharpToJava.Core.AsyncNormalize.AsyncStreams;
using CSharpToJava.Core.AsyncNormalize.Lambda;

namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// C18 async-normalize 公共入口。形态对齐 IteratorLoweringPreprocessor：
/// options 兜底 → mode 门控 → dirty check → analyze →（严格有错则短路返回）
/// → AsyncLambdaLifter（1b）→ AwaitShapeRewriter（1a，移动到新树的提升方法体一并被剥）
/// → Formatter.Format → 基于格式化后内存树的重解析 residual 闸门。轻量版不改写 await 本身。
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

        // Analyze（非严格：1801-1808 / 1841-1846 降 Warning，节点原样保留）。
        var analysis = AsyncNormalizeAnalyzer.Analyze(semanticModel, options);

        // C38 3a：await using / IAsyncDisposable 类型使用点分析（总闸开启时）。
        AsyncStreamAnalysisResult? streamAnalysis = null;
        if (options.EnableAsyncStreams)
            streamAnalysis = AsyncStreamAnalyzer.Analyze(semanticModel, options);

        var allAnalyses = streamAnalysis == null
            ? analysis.Diagnostics
            : analysis.Diagnostics.Concat(streamAnalysis.Diagnostics);
        foreach (var diagnostic in allAnalyses)
        {
            var severity = diagnostic.Severity;
            if (!options.Strict && severity == AsyncNormalizeSeverity.Error)
                severity = AsyncNormalizeSeverity.Warning;
            diagnostics.Add(diagnostic with { Severity = severity });
        }

        // 严格模式：任何 Error 级 skip 诊断 → 本文件整体不改写（Changed=false、
        // FilesDirtyNoRewrite、诊断照旧返回，树仍可编译）。
        var hasErrorSkip = diagnostics.Any(d => d.Severity == AsyncNormalizeSeverity.Error);

        var newRoot = root;
        var liftChanged = false;

        // 剥离计数/诊断由 lifter（原始树遍历，覆盖搬运进提升方法的体）与二趟
        // AwaitShapeRewriter（无提升/kill-switch 路径）共享，保证每站点只计一次。
        var configureAwait = options.StripConfigureAwait && !hasErrorSkip
            ? new ConfigureAwaitStripper()
            : null;

        if (!hasErrorSkip && options.LiftAsyncLambdas && analysis.Plans.Count > 0)
        {
            // 非严格：skip 节点原样保留，挂内存 skip 标注供 residual 白名单识别。
            var lifter = new AsyncLambdaLifter(
                semanticModel, analysis.Plans, annotateSkips: !options.Strict,
                configureAwait);
            lifter.Prepare();
            newRoot = (CompilationUnitSyntax)lifter.Visit(root)!;
            liftChanged = lifter.Changed;
        }
        else if (!hasErrorSkip && options.LiftAsyncLambdas && !options.Strict)
        {
            // 无计划但可能有 skip 节点（全部 defer）：仍走一遍 rewriter 挂内存 skip 标注，
            // 文本不变（annotation 不渲染），仅供 residual 闸门白名单使用。
            var annotator = new AsyncLambdaLifter(
                semanticModel, Array.Empty<LambdaLiftPlan>(), annotateSkips: true,
                configureAwait);
            newRoot = (CompilationUnitSyntax)annotator.Visit(root)!;
        }

        // ConfigureAwait(bool) 剥离二趟：lift 新树内的站点已在 lifter 中剥净，
        // 此处只在原始树/kill-switch 路径产生改写，对不可绑定节点安全跳过。
        var strippedCount = 0;
        if (configureAwait != null)
        {
            var rewriter = new AwaitShapeRewriter(semanticModel, configureAwait);
            newRoot = (CompilationUnitSyntax)rewriter.Visit(newRoot)!;
            strippedCount = configureAwait.ConfigureAwaitStripped;
            diagnostics.AddRange(configureAwait.Diagnostics);
        }

        // C38 3a：await using → try/finally + await DisposeAsync()。C18 提升/ConfigureAwait
        // 先跑（await using 体内被提升的 lambda 此时已是方法调用）；严格 Error 已整体短路。
        // 非严格：rejected/baseline 站点由 rewriter 原地挂 skip 标注、其余照常展开。
        AwaitUsingRewriter? streamRewriter = null;
        if (!hasErrorSkip && streamAnalysis != null
            && (streamAnalysis.Sites.Count > 0
                || streamAnalysis.RejectedNodes.Count > 0
                || streamAnalysis.BaselineExemptNodes.Count > 0))
        {
            streamRewriter = new AwaitUsingRewriter(streamAnalysis);
            newRoot = (CompilationUnitSyntax)streamRewriter.Visit(newRoot)!;
        }

        var changed = liftChanged || strippedCount > 0
            || (streamRewriter?.Changed ?? false);
        var outputCode = sourceCode;
        SyntaxNode gateRoot = newRoot;

        if (changed)
        {
            using var workspace = new AdhocWorkspace();
            var formatted = Formatter.Format(newRoot, workspace);
            gateRoot = formatted;
            outputCode = formatted.ToFullString();
            stats.FilesTransformed = 1;
        }
        else
        {
            // 仅诊断无改写 → 原样返回（gateRoot 可能带 skip 内存标注）。
            stats.FilesDirtyNoRewrite = 1;
        }

        // Statistics 桶累加。
        stats.AsyncMethodsSeen = analysis.AsyncMethodsSeen;
        stats.AwaitSites = analysis.AwaitSites;
        stats.ConfigureAwaitStripped = strippedCount;
        stats.AsyncVoidRejected = analysis.AsyncVoidCount;
        stats.UnsupportedAwaiters = analysis.UnsupportedAwaiters;
        stats.SkippedUnsupported = analysis.Diagnostics.Count;
        stats.CapturedAsyncLambdasDeferred = analysis.CapturedAsyncLambdasDeferred;

        // C38 3a 统计桶：放行/拒绝/鸭子计数来自分析（严格 Error 路径也如实计数，同 C18），
        // 展开/守卫桶仅在真正执行改写时累加（严格 Error 整体不改写，必须保持 0）。
        if (streamAnalysis != null)
        {
            stats.AsyncTypeSitesMapped = streamAnalysis.TypeSitesMapped;
            stats.AsyncTypeSitesRejected = streamAnalysis.TypeSitesRejected;
            stats.DuckTypedAsyncDeferred = streamAnalysis.DuckTypedDeferred;
            if (streamRewriter != null)
            {
                stats.AwaitUsingStatementsExpanded = streamRewriter.StatementsExpanded;
                stats.AwaitUsingDeclarationsExpanded = streamRewriter.DeclarationsExpanded;
                stats.AsyncDisposeGuardsEmitted = streamRewriter.GuardsEmitted;
            }
        }

        // 严格模式任何 Error 都导致整文件不改写：计划虽已分析但未执行，提升桶必须保持 0。
        if (!hasErrorSkip)
        {
            foreach (var plan in analysis.Plans)
            {
                if (plan.IsLocalFunction)
                {
                    stats.AsyncLocalFunctionsLifted++;
                    continue;
                }

                if (plan.NeedsWrapper)
                {
                    stats.AsyncLambdasLiftedVoidWrapper++;
                    stats.WrappersGenerated++;
                }
                else if (plan.IsStatic)
                {
                    stats.AsyncLambdasLiftedZeroCapture++;
                }
                else
                {
                    stats.AsyncLambdasLiftedThisOnly++;
                }
            }
        }

        // Residual 闸门：基于格式化后的内存 SyntaxTree（annotation 不跨文本存活，
        // 故白名单取自内存树，违规判定取重解析树 + 重绑编译）。
        var (c18Residual, c38Residual) = CountResidual(
            gateRoot, outputCode, semanticModel, options, changed);
        var residualCount = c18Residual + c38Residual;
        stats.ResidualCount = residualCount;
        if (c18Residual > 0 && options.Strict)
        {
            diagnostics.Add(new AsyncNormalizeDiagnostic(
                AsyncNormalizeSeverity.Error,
                AsyncNormalizeDiagnosticCode.CS2J1810,
                $"{c18Residual} residual async construct(s) remain after async normalization " +
                "(un-annotated async lambda/local function, ConfigureAwait call(s), or " +
                "un-exempted async void member(s))."));
        }

        // C38 residual：输出仍含未豁免的裸 await using（白名单=skip 诊断节点 +
        // annotation 合成节点）。合成树内 await DisposeAsync 是预期 await，不在此面计数；
        // 非严格不发 1849（Warning 站点已原样保留）。
        if (c38Residual > 0 && options.Strict)
        {
            diagnostics.Add(new AsyncNormalizeDiagnostic(
                AsyncNormalizeSeverity.Error,
                AsyncNormalizeDiagnosticCode.CS2J1849,
                $"{c38Residual} residual 'await using' site(s) remain after async-stream " +
                "normalization (un-exempted await using statement or declaration)."));
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
                // C38 3a：await using 语句/声明形态（await using 的 await 是关键字而非
                // AwaitExpressionSyntax，纯 await using 文件不会被上面的分支命中）。
                case UsingStatementSyntax awaitUsing
                    when awaitUsing.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    return true;
                case LocalDeclarationStatementSyntax awaitUsingDeclaration
                    when awaitUsingDeclaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                         && awaitUsingDeclaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    return true;
                // C38 3a：IAsyncDisposable 类型使用点（放行计数面，零改写也要过分析）。
                case IdentifierNameSyntax asyncDisposableIdentifier
                    when asyncDisposableIdentifier.Identifier.Text == "IAsyncDisposable":
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 残余闸门（设计 §5 AfterFilesAsync 的内存版、§6、D11）。
    /// 白名单：带 Cs2jAsyncNormalize annotation 的合成 wrapper/方法/C38 try-finally，以及
    /// 非严格模式下带 skip 标注的原样保留节点（1802/1804/1807/1841/1842/1845/基线站点）。
    /// C18 残余三类：① 未豁免 async lambda / 匿名方法 / 残余 async 局部函数；
    /// ② 未被 CS2J1808 覆盖的 ConfigureAwait(bool)；③ 未豁免 async void 成员。
    /// C38 残余：未豁免的裸 await using 语句/声明（合成的 await DisposeAsync 不在此面，
    /// 两个闸门互不计数对方产物）。返回 (C18 残余, C38 残余)。
    /// </summary>
    private static (int C18Residual, int C38Residual) CountResidual(
        SyntaxNode annotatedRoot,
        string outputCode,
        SemanticModel originalModel,
        AsyncNormalizeOptions options,
        bool outputChanged)
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

        // 白名单跨度取自格式化内存树（annotation 存活于节点，不存活于文本）。
        var skipWhitelist = new List<TextSpan>();
        var syntheticWhitelist = new List<TextSpan>();
        foreach (var node in annotatedRoot.DescendantNodesAndSelf())
        {
            if (node.HasAnnotations(AsyncNormalizeAnnotations.SkipAnnotationKind))
                skipWhitelist.Add(node.Span);
            if (node.HasAnnotations(AsyncNormalizeAnnotations.AnnotationKind))
                syntheticWhitelist.Add(node.Span);
        }

        var c18Residual = 0;

        // ① async lambda / 匿名方法 / async 局部函数（kill-switch 下整类不计）。
        if (options.LiftAsyncLambdas)
        {
            foreach (var node in outputRoot.DescendantNodes())
            {
                var isAsyncLambda = node switch
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
                if (isAsyncLambda
                    && !ContainedInAny(node.Span, skipWhitelist)
                    && !ContainedInAny(node.Span, syntheticWhitelist))
                {
                    c18Residual++;
                }
            }
        }

        if (outputModel != null)
        {
            // ② ConfigureAwait(bool)：options 形态已由 CS2J1808 覆盖，不计违规。
            foreach (var invocation in outputRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (AsyncShapeClassifier.IsConfigureAwaitBool(outputModel, invocation, out var isOptionsForm)
                    && !isOptionsForm)
                {
                    c18Residual++;
                }
            }

            // ③ 未豁免 async void（仅合成 annotation 可豁免；skip 节点不豁免）。
            foreach (var method in outputRoot.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (outputModel.GetDeclaredSymbol(method) is IMethodSymbol symbol
                    && symbol.IsAsync
                    && symbol.ReturnsVoid
                    && !ContainedInAny(method.Span, syntheticWhitelist))
                {
                    c18Residual++;
                }
            }

            foreach (var local in outputRoot.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {
                if (outputModel.GetDeclaredSymbol(local) is IMethodSymbol symbol
                    && symbol.IsAsync
                    && symbol.ReturnsVoid
                    && !ContainedInAny(local.Span, syntheticWhitelist))
                {
                    c18Residual++;
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
                    c18Residual++;
                }
            }
        }

        // C38 3a：未豁免的裸 await using 语句/声明。选项关闭时站点应已全部由
        // CS2J1841 承接（skip 白名单），闸门不再计数（同 C18 kill-switch 纪律）。
        var c38Residual = 0;
        if (options.EnableAsyncStreams && options.EnableAwaitUsing)
        {
            foreach (var node in outputRoot.DescendantNodes())
            {
                var isAwaitUsing = node switch
                {
                    UsingStatementSyntax awaitUsingStatement =>
                        awaitUsingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                    LocalDeclarationStatementSyntax awaitUsingDeclaration =>
                        awaitUsingDeclaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                        && awaitUsingDeclaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                    _ => false,
                };
                if (isAwaitUsing
                    && !ContainedInAny(node.Span, skipWhitelist)
                    && !ContainedInAny(node.Span, syntheticWhitelist))
                {
                    c38Residual++;
                }
            }
        }

        return (c18Residual, c38Residual);
    }

    private static bool ContainedInAny(TextSpan span, IReadOnlyList<TextSpan> whitelist)
    {
        foreach (var white in whitelist)
        {
            if (white.Start <= span.Start && span.End <= white.End)
                return true;
        }
        return false;
    }
}

public sealed record AsyncNormalizeFileResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<AsyncNormalizeDiagnostic> Diagnostics,
    AsyncNormalizeStatistics Statistics);
