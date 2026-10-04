using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using CSharpToJava.Core.AsyncNormalize.Await;
using CSharpToJava.Core.AsyncNormalize.AsyncStreams;
using CSharpToJava.Core.AsyncNormalize.AsyncStreams.AsyncIterator;
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

        // C38：span 漂移硬化（任务6）。C18 提升/ConfigureAwait 剥离可能插入方法、改动
        // 行号与节点身份，因此 stream 站点不在原始 model 上计划——直接把 C18 产物
        // newRoot（仍挂 C18 synthetic/skip 标注的内存树）经 CSharpSyntaxTree.Create
        // 包成正式 SyntaxTree，并 ReplaceSyntaxTree 重绑编译：post-C18 model 的查询
        // 节点与下游 rewriter 严格同树，引用/SpanStart 匹配天然成立，标注零丢失，
        // 不存在文本重解析往返。严格 Error 已在前面整体短路，不进此块。
        AwaitUsingRewriter? streamRewriter = null;
        AsyncForeachRewriter? foreachRewriter = null;
        AsyncIteratorLowering? iteratorLowering = null;

        if (!hasErrorSkip && streamAnalysis != null)
        {
            CompilationUnitSyntax postRoot;
            SemanticModel postModel;

            if (ReferenceEquals(newRoot, root))
            {
                // 无 C18 改写：原树原 model 直接复用，节点身份完全一致。
                postRoot = newRoot;
                postModel = semanticModel;
            }
            else if (semanticModel.Compilation is CSharpCompilation cSharpCompilation)
            {
                var parseOptions = semanticModel.SyntaxTree.Options as CSharpParseOptions;
                var postTree = CSharpSyntaxTree.Create(
                    newRoot, parseOptions, semanticModel.SyntaxTree.FilePath);
                var postCompilation = cSharpCompilation.ReplaceSyntaxTree(
                    semanticModel.SyntaxTree, postTree);
                postModel = postCompilation.GetSemanticModel(postTree);
                postRoot = newRoot;
            }
            else
            {
                // 非 CSharpCompilation 宿主（理论上测试夹具外不可达）：退化为原 model 直跑。
                postRoot = newRoot;
                postModel = semanticModel;
            }

            // 第二次分析仅用于 post-C18 计划；诊断仍以原始 model 的首轮分析为准
            // （行号归属原文，CS2J1830 等 Info 不重复发）。
            var postStreamAnalysis = AsyncStreamAnalyzer.Analyze(postModel, options);

            var hasUsingWork = postStreamAnalysis.Sites.Count > 0
                || postStreamAnalysis.RejectedNodes.Count > 0
                || postStreamAnalysis.BaselineExemptNodes.Count > 0;
            var hasForeachWork = options.EnableAsyncStreams
                && (postStreamAnalysis.ForeachSites.Count > 0
                    || postStreamAnalysis.ForeachRejectedNodes.Count > 0
                    || postStreamAnalysis.ForeachBaselineExemptNodes.Count > 0
                    || postStreamAnalysis.IteratorNodes.Count > 0);

            // 关键顺序：两个 rewriter 任一开始改写之前，先把两套站点/skip 标注都挂到
            // 输入树 green 层。await using 声明折叠会把后继语句重包进新 try（red 包装
            // 身份/SpanStart 全变），foreach 展开会搬运宿主块语句；green 标注随节点
            // 复用存活，两个 rewriter 都按标注定位计划，跨趟不丢站点。
            if (hasUsingWork)
            {
                postRoot = (CompilationUnitSyntax)
                    AwaitUsingRewriter.AttachSiteAnnotations(postRoot, postStreamAnalysis)!;
            }

            if (hasForeachWork)
            {
                postRoot = (CompilationUnitSyntax)
                    AsyncForeachRewriter.AttachSiteAnnotations(postRoot, postStreamAnalysis)!;
            }

            if (hasUsingWork)
            {
                streamRewriter = new AwaitUsingRewriter(postStreamAnalysis);
                postRoot = (CompilationUnitSyntax)streamRewriter.Visit(postRoot)!;
            }

            if (hasForeachWork)
            {
                foreachRewriter = new AsyncForeachRewriter(postStreamAnalysis, options);
                postRoot = (CompilationUnitSyntax)foreachRewriter.Visit(postRoot)!;
            }

            // C38 M4：await foreach/await using 站点展开后，第三次重绑模型，
            // 二趟 Planner 取计划（诊断以首轮为准，此处 sink 丢弃），跑联合状态机
            // lowering：async 工厂 + 生成枚举器类（+可枚举包装类）。首轮分析存在
            // 可降级计划时才花这次重绑/分析成本。
            if (streamAnalysis.IteratorPlans.Count > 0)
            {
                var boundTree = postModel.SyntaxTree;
                SemanticModel loweringModel = postModel;
                if (!ReferenceEquals(postRoot, boundTree.GetRoot()))
                {
                    if (postModel.Compilation is CSharpCompilation postCompilation)
                    {
                        var parseOptions = boundTree.Options as CSharpParseOptions;
                        var loweringTree = CSharpSyntaxTree.Create(
                            postRoot, parseOptions, boundTree.FilePath);
                        var loweringCompilation = postCompilation.ReplaceSyntaxTree(
                            boundTree, loweringTree);
                        loweringModel = loweringCompilation.GetSemanticModel(loweringTree);
                        // CSharpSyntaxTree.Create 会克隆根节点：Planner 返回的计划节点
                        // 属于克隆根，Lowering 按节点身份替换方法——必须在克隆根上改写。
                        postRoot = (CompilationUnitSyntax)loweringTree.GetRoot();
                    }
                }

                var asyncEnumerableInterface =
                    loweringModel.Compilation.GetTypeByMetadataName(
                        AsyncTypeResolver.AsyncEnumerableMetadataName);
                var asyncEnumeratorInterface =
                    loweringModel.Compilation.GetTypeByMetadataName(
                        AsyncTypeResolver.AsyncEnumeratorMetadataName);

                var iteratorPlans = AsyncIteratorPlanner.AnalyzePlans(
                    loweringModel, options, asyncEnumerableInterface, asyncEnumeratorInterface);
                if (iteratorPlans.Count > 0)
                {
                    iteratorLowering = new AsyncIteratorLowering(loweringModel);
                    postRoot = iteratorLowering.Rewrite(postRoot, iteratorPlans);
                }
            }

            newRoot = postRoot;
        }

        var changed = liftChanged || strippedCount > 0
            || (streamRewriter?.Changed ?? false)
            || (foreachRewriter?.Changed ?? false)
            || (iteratorLowering?.Changed ?? false);
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

        // C38 统计桶：放行/拒绝/鸭子/迭代器计数来自首轮分析（严格 Error 路径也如实计数，
        // 同 C18/3a），展开/守卫/链消解桶仅在真正执行改写时累加（严格 Error 整体不改写，
        // 展开桶必须保持 0）。
        if (streamAnalysis != null)
        {
            stats.AsyncTypeSitesMapped = streamAnalysis.TypeSitesMapped;
            stats.AsyncTypeSitesRejected = streamAnalysis.TypeSitesRejected;
            stats.DuckTypedAsyncDeferred = streamAnalysis.DuckTypedDeferred;
            stats.AsyncIteratorsRejected = streamAnalysis.IteratorsRejected;
            if (streamRewriter != null)
            {
                stats.AwaitUsingStatementsExpanded = streamRewriter.StatementsExpanded;
                stats.AwaitUsingDeclarationsExpanded = streamRewriter.DeclarationsExpanded;
                stats.AsyncDisposeGuardsEmitted = streamRewriter.GuardsEmitted;
            }
            if (foreachRewriter != null)
            {
                stats.AwaitForeachExpanded = foreachRewriter.Expanded;
                stats.AsyncEnumeratorsEmitted = foreachRewriter.EnumeratorsEmitted;
                stats.WithCancellationResolved = foreachRewriter.WithCancellationResolved;
                stats.EnumerableConfigureAwaitStripped = foreachRewriter.ConfigureAwaitStripped;
            }
            if (iteratorLowering != null)
            {
                stats.AsyncIteratorsLowered = iteratorLowering.IteratorsLowered;
                stats.AsyncIteratorClassesEmitted = iteratorLowering.ClassesEmitted;
                stats.AsyncIteratorRegionsEmitted = iteratorLowering.RegionsEmitted;
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
        stats.AsyncStreamResidualCount = c38Residual;
        if (c18Residual > 0 && options.Strict)
        {
            diagnostics.Add(new AsyncNormalizeDiagnostic(
                AsyncNormalizeSeverity.Error,
                AsyncNormalizeDiagnosticCode.CS2J1810,
                $"{c18Residual} residual async construct(s) remain after async normalization " +
                "(un-annotated async lambda/local function, ConfigureAwait call(s), or " +
                "un-exempted async void member(s))."));
        }

        // C38 residual：输出仍含未豁免的裸 await foreach / await using（白名单=skip
        // 诊断节点 + AS-6 冻结成员 + annotation 合成节点）。合成树内 await
        // MoveNextAsync/DisposeAsync 是预期 await，不在此面计数；非严格不发 1849
        // （Warning 站点已原样挂 skip 白名单；跨文件聚合兜底在适配器）。
        if (c38Residual > 0 && options.Strict)
        {
            diagnostics.Add(new AsyncNormalizeDiagnostic(
                AsyncNormalizeSeverity.Error,
                AsyncNormalizeDiagnosticCode.CS2J1849,
                $"{c38Residual} residual async-stream site(s) ('await foreach' or 'await using') " +
                "remain after async-stream normalization (un-exempted statement, declaration, " +
                "or frozen async-iterator member)."));
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
                // C38 3b：await foreach（await 是 foreach 的 AwaitKeyword，不含
                // AwaitExpressionSyntax；纯 await foreach 文件必须经分析）。
                case CommonForEachStatementSyntax awaitForeach
                    when awaitForeach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    return true;
                // C38 3b：异步流类型/取消链粗筛（符号判定在 Analyzer，此处只防干净短路）。
                case IdentifierNameSyntax asyncStreamIdentifier
                    when asyncStreamIdentifier.Identifier.Text is "IAsyncEnumerable"
                        or "IAsyncEnumerator"
                        or "ConfiguredCancelableAsyncEnumerable"
                        or "WithCancellation":
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

        // C38：未豁免的裸 await using（3a）/ await foreach（3b，含解构形态）。
        // 选项关闭时站点应已全部由 CS2J1841 承接（skip 白名单），闸门不再计数
        // （同 C18 kill-switch 纪律）；AS-6 冻结成员的 skip 标注按跨度整体豁免。
        var c38Residual = 0;
        if (options.EnableAsyncStreams)
        {
            foreach (var node in outputRoot.DescendantNodes())
            {
                var isAwaitUsing = options.EnableAwaitUsing && node switch
                {
                    UsingStatementSyntax awaitUsingStatement =>
                        awaitUsingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                    LocalDeclarationStatementSyntax awaitUsingDeclaration =>
                        awaitUsingDeclaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                        && awaitUsingDeclaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
                    _ => false,
                };

                var isAwaitForeach = options.EnableAwaitForeach
                    && node is CommonForEachStatementSyntax awaitForeachNode
                    && awaitForeachNode.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword);

                if ((isAwaitUsing || isAwaitForeach)
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
