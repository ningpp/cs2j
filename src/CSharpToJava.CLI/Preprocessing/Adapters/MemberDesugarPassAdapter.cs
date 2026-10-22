using System.Text;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the member-desugar pass (C11). Drives its own whole-tree
/// iteration copied structurally from java-keywords: each round replays the
/// originals → RewriteFile every file → recompile → diff against the baseline
/// error set → attribute new errors to plans → per-plan rollback; an
/// unattributable error rolls the whole run back (CS2J5100). Unlike
/// java-keywords, the tail restores and verifies build/tests like
/// make-readonly, then drops bin/ outputs (design §5.1).
/// </summary>
internal sealed class MemberDesugarPassAdapter : ProjectPassAdapter
{
    private readonly MemberDesugarOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly List<MemberDesugarDiagnostic> _diagnostics = new();
    private MemberDesugarStatistics _statistics = new();

    public MemberDesugarPassAdapter(
        MemberDesugarOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.MemberDesugar);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);

    public MemberDesugarStatistics Statistics => _statistics;
    public IReadOnlyList<MemberDesugarDiagnostic> Diagnostics => _diagnostics;

    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;
        var intermediateRoot = tree.IntermediateRoot;
        var sourceRoot = tree.SourceRoot;

        // Only .cs files participate; non-.cs project files were already copied by
        // CopyProjectFilesAsync during tree preparation.
        var sourceFiles = tree.CsFiles
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => (
                Source: file,
                Dest: Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, file))))
            .ToList();

        foreach (var pair in sourceFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Dest)!);
            tree.ResetScratchFile(pair.Source, pair.Dest);
        }

        // Preserve non-.cs assets (csproj, Directory.Build.*, ...). Tree preparation
        // copies them only when the project graph resolves; intermediate-tree input
        // often yields an empty graph, so the own loop must carry them itself.
        // .sln stays handled (and exclusion-rewritten) by tree preparation.
        foreach (var asset in tree.SourceFiles)
        {
            if (asset.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || asset.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assetDest = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, asset));
            Directory.CreateDirectory(Path.GetDirectoryName(assetDest)!);
            tree.ResetScratchFile(asset, assetDest);
        }

        // 整树输入编译 + 整树分析（plan）跨轮恒定：每轮都把全部文件重置回 pre-pass 字节，
        // 因此轮与轮之间只有 excluded 集合不同。编译/绑定一次完成，plan 跨轮复用——否则每轮
        // 重跑一次 4558-plan 整树分析（画像 2026-10-17：单轮分析 ~73s、改写 ~112s）。
        var inputCompilation = BuildCompilation(
            sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
            tree.Layout, intermediateRoot);
        var pristineTreesByPath = inputCompilation.SyntaxTrees
            .ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);
        var preprocessor = new MemberDesugarPreprocessor(_options);
        preprocessor.BindCompilation(inputCompilation);
        var plans = preprocessor.Plans;

        // 受影响文件集：全部 plan 触碰文件（声明 + 使用点 + 层次/接口联动）的并集。集合外的树
        // 在基线与候选间逐字节相同（候选甚至复用同一 tree 对象），其诊断两侧恒等、互相抵消，
        // 故候选 diff 与基线键都只需绑定集合内的树（扩展方法 pass 同款不变性论证 §4.4-4）。
        var affectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            foreach (var touched in plan.TouchedFiles)
            {
                affectedPaths.Add(Path.GetFullPath(touched));
            }
        }

        // 基线键：原始内容（即本轮输入）在受影响树上的逐树诊断，按树并行。
        var __baselinePathCache = new Dictionary<SyntaxTree, string>();
        var baselineErrors = new HashSet<(string File, string Id, string Message)>();
        var baselineFileIds = new HashSet<(string File, string Id)>();
        foreach (var diagnostic in CollectErrorDiagnostics(inputCompilation, affectedPaths))
        {
            var key = ErrorKey(diagnostic, intermediateRoot, __baselinePathCache);
            baselineErrors.Add(key);
            baselineFileIds.Add((key.File, key.Id));
        }

        // 候选编译的树自缓存：未改写文件复用 pre-pass tree，只有被改写的文件才重新解析。
        var globalUsingsTree = BuildGlobalUsingsTree(tree.Layout, intermediateRoot);
        var candidateTreesByPath = new Dictionary<string, SyntaxTree>(
            pristineTreesByPath, StringComparer.OrdinalIgnoreCase);
        var parseOptions = CreateParseOptions(tree);

        // 含终结器声明的文件单独串行处理：finalizer 阶段写入共享 sidecar（懒创建 + 逐条登记，
        // 非线程安全）；其余文件的逐文件改写互不共享可变状态，可并行。
        var destructorFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in sourceFiles)
        {
            if (pristineTreesByPath.TryGetValue(pair.Dest, out var pristine)
                && pristine.GetRoot().DescendantNodes().OfType<DestructorDeclarationSyntax>().Any())
            {
                destructorFiles.Add(pair.Source);
            }
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var rollbackDiagnostics = new List<MemberDesugarDiagnostic>();
        var acceptedStats = new MemberDesugarStatistics();
        var acceptedDiagnostics = new List<MemberDesugarDiagnostic>();
        var success = true;
        const int maxIterations = 1024;
        IReadOnlyList<MemberDesugarPlan>? lastPlans = null;

        // 性能：跨迭代复用文件改写产物。一个文件的产出只取决于「其 TouchedFiles 中已被排除
        // 的 plan 集合」；该集合未变的文件产出确定且逐字节不变，直接复用上一迭代已落盘的输出
        // 与候选树，无需重跑改写。仅当某 plan 被回退（excluded 增长）才把其触碰文件标为需重写。
        var destIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sourceFiles.Count; index++)
            destIndex[Path.GetFullPath(sourceFiles[index].Dest)] = index;
        var outcomes = new FileRewriteOutcome[sourceFiles.Count];
        var needsRewrite = new bool[sourceFiles.Count];

        // 性能：预初始化不受影响文件的 outcomes 为 clean 结果，避免 NRE 并省掉
        // RewriteFile 调用（画像 2026-10-18：932 文件中 ~852 不受影响）。
        for (var index = 0; index < sourceFiles.Count; index++)
        {
            var dest = Path.GetFullPath(sourceFiles[index].Dest);
            if (!affectedPaths.Contains(dest))
            {
                outcomes[index] = new FileRewriteOutcome
                {
                    Result = new MemberDesugarFileResult
                    {
                        OutputCode = File.ReadAllText(sourceFiles[index].Dest),
                        Changed = false,
                    },
                    Statistics = new MemberDesugarStatistics(),
                    Diagnostics = new List<MemberDesugarDiagnostic>(),
                };
            }
        }

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            if (iteration == 0)
            {
                // 只对受影响文件（plan 触碰的）做改写；集合外的树在基线与候选间
                // 逐字节相同，改写结果恒为 Changed=false。
                for (var index = 0; index < sourceFiles.Count; index++)
                {
                    var dest = Path.GetFullPath(sourceFiles[index].Dest);
                    needsRewrite[index] = affectedPaths.Contains(dest);
                }
            }

            // BeginIteration resets the shared finalizer sidecar, so every
            // destructor-host file with active plans must replay each round;
            // otherwise later iterations lose its earlier sidecar entries.
            for (var index = 0; index < sourceFiles.Count; index++)
            {
                if (destructorFiles.Contains(sourceFiles[index].Source)
                    && affectedPaths.Contains(Path.GetFullPath(sourceFiles[index].Dest)))
                    needsRewrite[index] = true;
            }

            // 每轮从原始副本出发（仅对需重写者）：避免对已改写文件二次驱动造成幂等问题。
            for (var index = 0; index < sourceFiles.Count; index++)
            {
                if (!needsRewrite[index]) continue;
                var pair = sourceFiles[index];
                tree.ResetScratchFile(pair.Source, pair.Dest);
            }

            preprocessor.ExcludePlans(excluded);
            preprocessor.BeginIteration();
            lastPlans = plans;

            // 逐文件改写纯计算并行（终结器宿主文件串行），随后按源文件序串行合并：写盘、
            // 统计并入、诊断收集、verbose 行打印——可观察输出与串行循环逐字节一致
            // （扩展方法 pass 同款纪律）。仅需重写者进入本阶段。
            Parallel.For(
                0,
                sourceFiles.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                index =>
                {
                    if (!needsRewrite[index]) return;
                    var pair = sourceFiles[index];
                    if (destructorFiles.Contains(pair.Source))
                    {
                        return;
                    }

                    outcomes[index] = RewriteOneFile(
                        pair, preprocessor, inputCompilation, pristineTreesByPath);
                });

            for (var index = 0; index < sourceFiles.Count; index++)
            {
                if (!needsRewrite[index]) continue;
                var pair = sourceFiles[index];
                if (!destructorFiles.Contains(pair.Source))
                {
                    continue;
                }

                outcomes[index] = RewriteOneFile(
                    pair, preprocessor, inputCompilation, pristineTreesByPath);
            }

            var passStats = new MemberDesugarStatistics();
            var passDiagnostics = new List<MemberDesugarDiagnostic>();
            for (var index = 0; index < sourceFiles.Count; index++)
            {
                var pair = sourceFiles[index];
                var outcome = outcomes[index];
                passStats.Add(outcome.Statistics);
                passDiagnostics.AddRange(outcome.Diagnostics);

                if (!needsRewrite[index])
                {
                    continue; // 复用上一迭代已落盘的输出与候选树
                }

                if (outcome.Result.Changed)
                {
                    File.WriteAllText(pair.Dest, outcome.Result.OutputCode,
                        FileStartsWithBom(pair.Source)
                            ? new UTF8Encoding(true)
                            : new UTF8Encoding(false));
                    candidateTreesByPath[pair.Dest] = CSharpSyntaxTree.ParseText(
                        outcome.Result.OutputCode, parseOptions, path: pair.Dest);
                }
                else
                {
                    candidateTreesByPath[pair.Dest] = pristineTreesByPath[pair.Dest];
                }

                if (_verbose)
                {
                    Console.WriteLine(
                        $"[member-desugar] {(outcome.Result.Changed ? "transformed" : "clean (copy)")}: " +
                        $"{Path.GetRelativePath(sourceRoot, pair.Source)}");
                }
            }

            var candidateTrees = new List<SyntaxTree>(sourceFiles.Count + 1);
            foreach (var pair in sourceFiles)
                candidateTrees.Add(candidateTreesByPath[pair.Dest]);
            if (globalUsingsTree != null)
                candidateTrees.Add(globalUsingsTree);

            var candidateCompilation = CreateCompilation(candidateTrees, tree.Layout);
            var __candidatePathCache = new Dictionary<SyntaxTree, string>();
            var candidateDiags = CollectErrorDiagnostics(candidateCompilation, affectedPaths);
            // 性能：(file, id) 不在基线键集合内者可判定为新增，无需计算 GetMessage
            // （本地化消息构造较贵）；(file, id) 命中基线时才比较消息。
            var newErrors = candidateDiags
                .Where(d =>
                {
                    var file = CachedNormalizedPath(d, intermediateRoot, __candidatePathCache);
                    if (!baselineFileIds.Contains((file, d.Descriptor.Id)))
                        return true;
                    return !baselineErrors.Contains((file, d.Descriptor.Id, d.GetMessage()));
                })
                .ToList();

            if (newErrors.Count == 0)
            {
                acceptedStats = passStats;
                acceptedDiagnostics = passDiagnostics;
                break;
            }

            // 归属新增错误：取该位置的 owner（含已排除）中的【活动】者作回退集——同位置的
            // 多个 owner（基/派生、属性/签名 plan、实现/接口）整组回退才能恢复一致。
            // 活动集为空（仅归属于已排除 plan，或位置不属于任何 plan——错误锚点是使用点/
            // 类型声明，其 (类型名,成员名) 键落不到生成该节点的 plan 上）则本轮无可回退的
            // 活动 plan 能消除它，接受本轮；真实 breakage 由尾部 msbuild 验证兜底。
            var plansToRollback = new HashSet<string>(StringComparer.Ordinal);
            var blockedByExcludedCount = 0;
            foreach (var error in newErrors)
            {
                var activeOwners = preprocessor.CollectOwners(error, out var owners)
                    ? owners.Where(id => !excluded.Contains(id)).ToList()
                    : new List<string>();
                if (activeOwners.Count > 0)
                {
                    foreach (var owner in activeOwners)
                        plansToRollback.Add(owner);
                    continue;
                }

                blockedByExcludedCount++;
            }

            if (plansToRollback.Count == 0)
            {
                if (blockedByExcludedCount == newErrors.Count)
                {
                    // 全部新错误仅锚定于已回退 plan：接受本轮（避免镜像碰撞导致
                    // 全树有效改写被连带回退的过保守行为，画像 2026-10-09）。
                    acceptedStats = passStats;
                    acceptedDiagnostics = passDiagnostics;
                    break;
                }

                // 无法归属单一 plan → 回退本次运行全部改动（CS2J5100）。
                foreach (var pair in sourceFiles)
                    tree.ResetScratchFile(pair.Source, pair.Dest);
                var first = newErrors[0];
                var span = first.Location.GetLineSpan().StartLinePosition;
                _diagnostics.Add(new MemberDesugarDiagnostic(
                    MemberDesugarSeverity.Error,
                    MemberDesugarDiagnosticCode.WholeTreeRollback,
                    "whole-tree validation failed and the new error could not be attributed to a " +
                    $"single plan ({first.Descriptor.Id}: {first.GetMessage()}); all changes " +
                    "from this run were rolled back.",
                    first.Location.SourceTree?.FilePath,
                    span.Line + 1,
                    span.Character + 1,
                    null));
                acceptedStats = new MemberDesugarStatistics();
                acceptedDiagnostics = new List<MemberDesugarDiagnostic>();
                success = false;
                break;
            }


            // 下一迭代只需重写本轮回退 plan 触碰的文件；其余文件的产出与上一迭代逐字节
            // 相同（其已排除 plan 集合未变），直接复用。
            var nextNeedsRewrite = new bool[sourceFiles.Count];
            foreach (var planId in plansToRollback)
            {
                var plan = preprocessor.Plans.First(p => p.Id == planId);
                excluded.Add(planId);
                acceptedStats.PlansRolledBack++;
                rollbackDiagnostics.Add(new MemberDesugarDiagnostic(
                    MemberDesugarSeverity.Warning,
                    MemberDesugarDiagnosticCode.PlanRolledBack,
                    "whole-tree validation attributed a new error to this plan; the plan " +
                    "and all of its touched files were rolled back atomically.",
                    plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
                    (plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)
                        ?.GetLineSpan().StartLinePosition.Line ?? 0) + 1,
                    (plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)
                        ?.GetLineSpan().StartLinePosition.Character ?? 0) + 1,
                    plan.SymbolName));

                // 回退该计划触碰的所有文件（可能与其他计划共享文件；下一轮重放这些文件）。
                foreach (var touched in plan.TouchedFiles)
                {
                    if (destIndex.TryGetValue(Path.GetFullPath(touched), out var fileIndex))
                        nextNeedsRewrite[fileIndex] = true;
                }
            }

            // 最后一轮仍失败：回退全部。
            if (iteration == maxIterations - 1)
            {
                foreach (var pair in sourceFiles)
                    tree.ResetScratchFile(pair.Source, pair.Dest);
                success = false;
            }

            needsRewrite = nextNeedsRewrite;
        }

        _diagnostics.AddRange(rollbackDiagnostics);
        _diagnostics.AddRange(acceptedDiagnostics);
        _statistics = acceptedStats;

        // C13：事件改写产物引用 Cs2jPrelude.CsPrelude.Combine/Remove——有生效事件 plan 时
        // 条件释放支撑工程并向含事件触碰文件的项目注入 ProjectReference（设计 §3.2-3）。
        // 默认链中 delegate pass 先行已在场；此处兜底覆盖单 pass / 边角配置。
        if (success && lastPlans != null)
        {
            var eventTouchedFiles = lastPlans
                .OfType<CSharpToJava.Core.MemberDesugar.Events.EventPlan>()
                .Where(p => p.Outcome == MemberDesugarOutcome.Planned && !excluded.Contains(p.Id))
                .SelectMany(p => p.TouchedFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (eventTouchedFiles.Count > 0)
            {
                await PreludeSupportFiles.ReleaseAsync(
                    Path.Combine(intermediateRoot, PreludeSupportFiles.LibDirectoryName));
                EnsureCs2jPreludeProjectReferences(tree, eventTouchedFiles);
            }
        }

        // make-readonly 尾段（设计 §5.1）：还原包 → 验证构建/测试 → 清理 bin 输出。
        // 还原仅为验证构建/测试服务：两者皆被关闭（--no-verify-*）时跳过 restore，
        // 避免 RC 之后的整个解决方案还原（画像 2026-10-17）。
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(tree.Layout, intermediateRoot);
        if (restoreTarget != null && (VerifyBuild || VerifyTests))
        {
            context.Restore();
            await context.VerifyBuildAndTestsAsync();
            ProjectPassRunner.DeleteBuildOutputs(intermediateRoot);
        }

        context.Success = success;
    }

    /// <summary>单文件改写的一次纯计算产出（并行阶段用；无共享可变状态写入）。</summary>
    private sealed class FileRewriteOutcome
    {
        public required MemberDesugarFileResult Result { get; init; }
        public required MemberDesugarStatistics Statistics { get; init; }
        public required List<MemberDesugarDiagnostic> Diagnostics { get; init; }
    }

    /// <summary>
    /// 逐文件改写：独立 statistics/diagnostics，语义模型取自跨轮复用的输入编译（pre-pass
    /// 内容），因此产出只依赖该文件自身文本 + 当前活动 plan 集合。终结器宿主文件由调用方
    /// 串行驱动（sidecar 非线程安全）。
    /// </summary>
    private static FileRewriteOutcome RewriteOneFile(
        (string Source, string Dest) pair,
        MemberDesugarPreprocessor preprocessor,
        CSharpCompilation inputCompilation,
        Dictionary<string, SyntaxTree> pristineTreesByPath)
    {
        var statistics = new MemberDesugarStatistics();
        var diagnostics = new List<MemberDesugarDiagnostic>();
        var sourceCode = File.ReadAllText(pair.Dest);
        if (!pristineTreesByPath.TryGetValue(pair.Dest, out var syntaxTree))
        {
            return new FileRewriteOutcome
            {
                Result = new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false },
                Statistics = statistics,
                Diagnostics = diagnostics,
            };
        }

        var result = preprocessor.RewriteFile(
            sourceCode,
            inputCompilation.GetSemanticModel(syntaxTree),
            statistics,
            diagnostics);
        return new FileRewriteOutcome
        {
            Result = result,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("MemberDesugarPassAdapter drives the file loop itself.");

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new MemberDesugarDiagnostic(
            MemberDesugarSeverity.Error,
            MemberDesugarDiagnosticCode.RestoreFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            null, 0, 0, null));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new MemberDesugarDiagnostic(
            MemberDesugarSeverity.Error,
            MemberDesugarDiagnosticCode.BuildFailed,
            $"dotnet build failed with exit code {exitCode} after member desugaring. " +
            "The property rewrite produced code that does not compile.",
            null, 0, 0, null));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new MemberDesugarDiagnostic(
            MemberDesugarSeverity.Error,
            MemberDesugarDiagnosticCode.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after member desugaring.",
            null, 0, 0, null));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == MemberDesugarSeverity.Error);

    public override string BuildChainSummaryLine()
    {
        _statistics.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualProperty, out var residual);
        _statistics.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualIndexer, out var indexerResidual);
        _statistics.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualEvent, out var residualEvent);
        _statistics.SkippedByReason.TryGetValue(MemberDesugarDiagnosticCode.ResidualOperator, out var residualOp);
        return $"member-desugar: {_statistics.PropertiesPlanned} propert(y/ies) planned, " +
               $"{_statistics.BackingFieldsGenerated} field(s) generated, " +
               $"{_statistics.ReadSitesRewritten} read/{_statistics.WriteSitesRewritten} write/" +
               $"{_statistics.CompoundSitesRewritten} compound site(s) rewritten, " +
               $"{_statistics.InitializersRewritten} initializer(s), " +
               $"{_statistics.IndexersPlanned} indexer(s) planned, " +
               $"{_statistics.IndexerReadSitesRewritten} index read/" +
               $"{_statistics.IndexerWriteSitesRewritten} write/" +
               $"{_statistics.IndexerCompoundSitesExpanded} compound site(s) rewritten, " +
               $"{_statistics.IndexerInitializerSitesRewritten} index initializer(s), " +
               $"{_statistics.EventsPlanned} event(s) planned " +
               $"({_statistics.EventBackingFieldsGenerated} field(s), " +
               $"{_statistics.EventSubscribeSitesRewritten}+/{_statistics.EventUnsubscribeSitesRewritten}- subscribe, " +
               $"{_statistics.EventInClassCombineSitesRewritten} in-class combine, " +
               $"{_statistics.EventNameofLiteralized} nameof), " +
               $"{_statistics.PlansSkipped} skipped, " +
               $"{_statistics.PlansRolledBack} rolled back, " +
               $"{residual} residual, {indexerResidual} indexer residual, {residualEvent} event residual" +
               $" | operators: {_statistics.OperatorsPlanned} planned, " +
               $"{_statistics.OperatorBinarySitesRewritten} binary/" +
               $"{_statistics.OperatorUnarySitesRewritten} unary/" +
               $"{_statistics.OperatorCompoundSitesRewritten} compound/" +
               $"{_statistics.OperatorConversionSitesRewritten} conversion site(s), " +
               $"{_statistics.OperatorLiftedSitesExpanded} lifted, " +
               $"{_statistics.GateOperatorsRetained} gate-retained, " +
               $"{_statistics.IncrementDecrementDeferred} deferred(++/--), " +
               $"{_statistics.MetadataOperatorSitesUntouched} metadata-only, " +
               $"{residualOp} operator-residual" +
               // C39（M2，设计 §5.3）：finalizers 段追加在末尾，不破坏既有字段。
               $" | finalizers: {_statistics.FinalizersDetected} detected " +
               $"(empty {_statistics.FinalizerEmpty}/dispose-pattern {_statistics.FinalizerDisposePattern}/" +
               $"safe-handle {_statistics.FinalizerSafeHandleOnly}/native {_statistics.FinalizerNativeRelease}/" +
               $"other {_statistics.FinalizerOther}/extern {_statistics.FinalizerExtern}), " +
               $"{_statistics.FinalizersReported} reported, " +
               $"{_statistics.FinalizersRemoved} removed " +
               $"({_statistics.FinalizersForceRemoved} forced; sidecar: " +
               $"{_statistics.FinalizerSidecarPath ?? "none"}), " +
               $"{_statistics.FinalizersRefused + _statistics.FinalizerCriticalBlocked} blocked, " +
               $"{_statistics.FinalizerResidual} residual, " +
               $"{_statistics.FinalizerResurrectionSites} resurrection-site(s)" +
               // C46（M2，设计 §5.2）：signatures 段追加在末尾，不破坏既有字段。
               $" | signatures: {_statistics.SignaturePlansPromoted} explicit promoted, " +
               $"{_statistics.SignatureDisplacedRenamed} displaced renamed, " +
               $"{_statistics.SignatureDualInterfaceMerged} dual-interface merged, " +
               $"{_statistics.SignatureCallSitesRewritten} call-site(s) rewritten, " +
               $"{_statistics.SignatureHideRenamed} hide renamed, " +
               $"{_statistics.SignatureHideBridged} hide bridged, " +
               // C46（M4b，设计 §5.2）：协变桥接段追加在末尾，不破坏既有字段。
               $"{_statistics.SignatureCovariantBridgesSynthesized} covariant bridge(s), " +
               $"{_statistics.SignatureCovariantRenamed} covariant renamed, " +
               $"{_statistics.SignatureReflectionWarnings} reflection-warned, " +
               $"{_statistics.SignatureResidual} residual" +
               // C50（设计 §5）：field-keyword 片段追加在末尾，关闭时全 0。
               $" | field-keyword: {_statistics.FieldKeywordProperties} properties normalized, " +
               $"{_statistics.FieldKeywordAutoAccessorsExpanded} auto accessor(s) expanded, " +
               $"{_statistics.FieldReferencesRewritten} reference(s), " +
               $"{_statistics.FieldTargetAttributesMigrated} field-attribute(s), " +
               $"{_statistics.FieldInitializersMoved} initializer(s), " +
               $"{_statistics.FieldKeywordResidualTokens} residual" +
               // C27（M1，设计 §5.3）：records 段追加在末尾，Records 子集关闭时全 0。
               $" | records: {_statistics.RecordPlans} plan(s) " +
               $"({_statistics.RecordClassPlans} record class), " +
               $"{_statistics.CloneFactoriesSynthesized} clone factor(ies), " +
               $"{_statistics.WithSitesTotal} with-site(s) " +
               $"({_statistics.WithSitesClass} class/{_statistics.WithSitesEmpty} empty), " +
               $"{_statistics.PropertyAssignments} property/{_statistics.FieldAssignments} field " +
               "assignment(s), " +
               $"{_statistics.TemporariesPromoted} temp(s), " +
               $"{_statistics.ResidualWithCount} residual";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != MemberDesugarSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private static CSharpCompilation BuildCompilation(
        IEnumerable<(string Path, string Content)> files,
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot)
    {
        var trees = new List<SyntaxTree>();
        foreach (var (path, content) in files)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                content,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: path));
        }

        var globalUsingsTree = BuildGlobalUsingsTree(layout, intermediateRoot);
        if (globalUsingsTree != null)
        {
            trees.Add(globalUsingsTree);
        }

        return CreateCompilation(trees, layout);
    }

    /// <summary>合成 <c>__cs2j_global_usings.g.cs</c> 树（无隐式 using 时为 null），跨轮复用同一对象。</summary>
    private static SyntaxTree? BuildGlobalUsingsTree(
        ProjectPreprocessSourceLayout layout, string intermediateRoot)
    {
        var globalUsings = new StringBuilder();
        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(intermediateProjectFile, intermediateRoot);
            if (projectUsings != null) globalUsings.AppendLine(projectUsings);
        }

        return globalUsings.Length == 0
            ? null
            : CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: "__cs2j_global_usings.g.cs");
    }

    private static CSharpCompilation CreateCompilation(
        IEnumerable<SyntaxTree> trees, ProjectPreprocessSourceLayout layout)
        => CSharpCompilation.Create(
            "member-desugar-preprocess",
            trees,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    // ---- C13：Cs2jPrelude 在场保证（设计 §3.2-3，镜像 DelegatePassAdapter 注入形态）----

    private void EnsureCs2jPreludeProjectReferences(PassTreeContext tree, IReadOnlyList<string> touchedFiles)
    {
        var libProjectPath = Path.Combine(
            tree.IntermediateRoot, PreludeSupportFiles.LibDirectoryName, "Cs2jPrelude.csproj");
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, tree.Exclusion))
            {
                continue;
            }
            var destinationProjectFile = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, projectFile));
            // touchedFiles 是中间树文件路径，必须与中间工程目录匹配。
            var destinationProjectDirectory = Path.GetDirectoryName(destinationProjectFile)!;
            if (!touchedFiles.Any(file => ProjectPreprocessSource.IsSameOrDescendantPath(file, destinationProjectDirectory)))
            {
                continue;
            }
            EnsureCs2jPreludeProjectReference(destinationProjectFile, libProjectPath, tree.IntermediateRoot);
        }
    }

    private static void EnsureCs2jPreludeProjectReference(
        string projectFilePath, string libProjectPath, string intermediateRoot)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        System.Xml.Linq.XDocument document;
        try
        {
            document = System.Xml.Linq.XDocument.Load(projectFilePath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;

        // 释放的 Cs2jPrelude/ 源码目录不能被宿主项目的默认 Compile 项重复编译。
        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFullPath(projectDirectory).TrimEnd('\\', '/'),
                Path.GetFullPath(intermediateRoot).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            var removePattern = Path.Combine(PreludeSupportFiles.LibDirectoryName, "**");
            if (!root.Descendants().Any(e =>
                    e.Name.LocalName == "Compile"
                    && string.Equals((string?)e.Attribute("Remove"), removePattern, StringComparison.OrdinalIgnoreCase)))
            {
                root.Add(
                    new System.Xml.Linq.XText(Environment.NewLine + "  "),
                    new System.Xml.Linq.XElement("ItemGroup",
                        new System.Xml.Linq.XElement("Compile", new System.Xml.Linq.XAttribute("Remove", removePattern))),
                    new System.Xml.Linq.XText(Environment.NewLine));
            }
        }

        foreach (var referenceElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference")
                     .ToList())
        {
            var include = (string?)referenceElement.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }
            var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include!));
            if (string.Equals(
                    Path.GetFullPath(resolvedPath).TrimEnd('\\', '/'),
                    Path.GetFullPath(libProjectPath).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                referenceElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, libProjectPath);
        root.Add(
            new System.Xml.Linq.XText(Environment.NewLine + "  "),
            new System.Xml.Linq.XElement("ItemGroup",
                new System.Xml.Linq.XElement("ProjectReference", new System.Xml.Linq.XAttribute("Include", relativeInclude))),
            new System.Xml.Linq.XText(Environment.NewLine));

        document.Save(projectFilePath, System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    private static string? GetPropertyValue(System.Xml.Linq.XElement root, string propertyName)
        => root.Descendants()
            .Where(e => e.Name.LocalName == "PropertyGroup")
            .SelectMany(pg => pg.Elements())
            .Where(e => e.Name.LocalName.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value)
            .FirstOrDefault();

    /// <summary>
    /// 受影响文件（∩ 传入编译的树）上的逐树 Error 诊断，按树序（= 编译 SyntaxTrees 序）
    /// 拼接——与整编译 <c>GetDiagnostics()</c> 的树内枚举序一致，故 diff 结果与逐字节基线
    /// 相同。集合外的树在基线与候选间逐字节相同，其诊断两侧恒等、互相抵消（参见调用点论证）。
    /// </summary>
    private static List<Diagnostic> CollectErrorDiagnostics(
        CSharpCompilation compilation,
        HashSet<string> affectedPaths)
    {
        var trees = compilation.SyntaxTrees as IReadOnlyList<SyntaxTree>
            ?? compilation.SyntaxTrees.ToList();
        var slots = new List<Diagnostic>?[trees.Count];
        Parallel.For(0, trees.Count, i =>
        {
            var tree = trees[i];
            if (!affectedPaths.Contains(Path.GetFullPath(tree.FilePath)))
            {
                return;
            }

            var model = compilation.GetSemanticModel(tree);
            var list = new List<Diagnostic>();
            foreach (var diagnostic in model.GetDiagnostics())
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error
                    && diagnostic.Location.SourceTree != null)
                {
                    list.Add(diagnostic);
                }
            }

            slots[i] = list;
        });

        var result = new List<Diagnostic>();
        foreach (var slot in slots)
        {
            if (slot != null)
            {
                result.AddRange(slot);
            }
        }

        return result;
    }

    /// <summary>性能：把路径归一化结果按树缓存，避免逐诊断重复 <c>GetFullPath</c>+<c>GetRelativePath</c>
    /// （每次 diff 要对全部候选诊断计算键）。</summary>
    private static string CachedNormalizedPath(
        Diagnostic diagnostic, string intermediateRoot, Dictionary<SyntaxTree, string> pathCache)
    {
        var tree = diagnostic.Location.SourceTree!;
        if (!pathCache.TryGetValue(tree, out var normalized))
        {
            normalized = Normalize(tree.FilePath, intermediateRoot);
            pathCache[tree] = normalized;
        }

        return normalized;
    }

    private static (string File, string Id, string Message) ErrorKey(
        Diagnostic diagnostic, string intermediateRoot, Dictionary<SyntaxTree, string> pathCache)
        => (CachedNormalizedPath(diagnostic, intermediateRoot, pathCache),
            diagnostic.Descriptor.Id,
            diagnostic.GetMessage());

    private static string Normalize(string path, string intermediateRoot)
    {
        var full = Path.GetFullPath(path);
        return Path.GetRelativePath(intermediateRoot, full).Replace('\\', '/');
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }
}
