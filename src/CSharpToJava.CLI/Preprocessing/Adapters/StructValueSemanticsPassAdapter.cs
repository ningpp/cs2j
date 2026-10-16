using System.Text;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the struct-value-semantics pass (C07). Drives its own whole-tree
/// iteration exactly like record-readonly: each round replays originals →
/// BindCompilation → RewriteFile every file → recompile → diff against the baseline
/// error set → attribute new errors to struct plans → rollback (SV030/SV031,
/// design §4.2/§5). Tree preparation and restore reuse the runner stage helpers.
/// </summary>
internal sealed class StructValueSemanticsPassAdapter : ProjectPassAdapter
{
    private readonly StructValueSemanticsOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly List<StructValueSemanticsDiagnostic> _diagnostics = new();
    private StructValueSemanticsStatistics _statistics = new();

    public StructValueSemanticsPassAdapter(
        StructValueSemanticsOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.StructValueSemantics);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override string CompilationName => "struct-value-semantics-preprocess";

    public StructValueSemanticsStatistics Statistics => _statistics;
    public IReadOnlyList<StructValueSemanticsDiagnostic> Diagnostics => _diagnostics;

    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;
        var intermediateRoot = tree.IntermediateRoot;
        var sourceRoot = tree.SourceRoot;

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

        // Preserve non-.cs assets (csproj, Directory.Build.*, ...); .sln stays handled
        // by tree preparation (same rationale as record-readonly).
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

        // 基线编译：内容取原始文件、但 path 取中间树路径，保证错误键可直接比对。
        // 该编译供第 0 轮复用（scratch 刚被重置为原始字节，内容逐字节相同），
        // 省去一次全量 parse 与绑定。
        var baselineCompilation = BuildCompilation(
            sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Source))),
            tree.Layout, intermediateRoot);
        // 基线错误集惰性求值：仅当确有文件被改写、需要与候选编译比对时才整树 GetDiagnostics。
        // 无改写时（本 pass 常见情形）该集合无人消费，避免一次昂贵的全量绑定。
        HashSet<(string File, string Id, string Message)>? baselineErrors = null;

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var rollbackDiagnostics = new List<StructValueSemanticsDiagnostic>();
        var acceptedStats = new StructValueSemanticsStatistics();
        var acceptedDiagnostics = new List<StructValueSemanticsDiagnostic>();
        var success = true;
        const int maxIterations = 1024;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // 每轮从原始副本出发：避免对已改写文件二次驱动造成幂等问题。
            foreach (var pair in sourceFiles)
                tree.ResetScratchFile(pair.Source, pair.Dest);

            var passStats = new StructValueSemanticsStatistics();
            var passDiagnostics = new List<StructValueSemanticsDiagnostic>();
            var preprocessor = new StructValueSemanticsPreprocessor(_options);
            // 第 0 轮复用基线编译（内容逐字节相同，见上）；后续轮次因改写/回退重建。
            var compilation = iteration == 0
                ? baselineCompilation
                : BuildCompilation(
                    sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                    tree.Layout, intermediateRoot);
            preprocessor.BindCompilation(compilation);
            preprocessor.ExcludePlans(excluded);
            // 计划级产物（聚合计数 + 计划期诊断）即使全体文件 clean 也要暴露一次；
            // 显式并入后逐文件 RewriteFile 不再触发（幂等）。
            preprocessor.MergePlanningOutput(passStats, passDiagnostics);

            // 仅对「被活跃 plan 触碰」的文件构造 SemanticModel 并重写；其余文件必然 clean
            // （其命中口径与 RewriteFile 内 activePlans 判定逐字相同），跳过读取/建模/遍历。
            var activeTouchedFiles = preprocessor.ActiveTouchedFiles();
            var treesByPath = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidateTree in compilation.SyntaxTrees)
                treesByPath.TryAdd(candidateTree.FilePath, candidateTree);

            var rewrittenByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sourceFiles)
            {
                if (!treesByPath.TryGetValue(pair.Dest, out var syntaxTree)) continue;

                if (!activeTouchedFiles.Contains(pair.Dest))
                {
                    passStats.FilesScanned++;
                    if (_verbose)
                    {
                        Console.WriteLine(
                            "[struct-value-semantics] clean (copy): " +
                            $"{Path.GetRelativePath(sourceRoot, pair.Source)}");
                    }
                    continue;
                }

                var sourceCode = File.ReadAllText(pair.Dest);

                var result = preprocessor.RewriteFile(
                    sourceCode,
                    compilation.GetSemanticModel(syntaxTree),
                    passStats,
                    passDiagnostics);

                if (result.Changed)
                {
                    File.WriteAllText(pair.Dest, result.OutputCode,
                        FileStartsWithBom(pair.Source)
                            ? new UTF8Encoding(true)
                            : new UTF8Encoding(false));
                    rewrittenByFile[pair.Dest] = pair.Source;
                }

                if (_verbose)
                {
                    Console.WriteLine(
                        $"[struct-value-semantics] {(result.Changed ? "transformed" : "clean (copy)")}: " +
                        $"{Path.GetRelativePath(sourceRoot, pair.Source)}");
                }
            }

            // 本轮无文件被改写时，scratch 内容与基线逐字节相同，候选编译的错误必然
            // 与基线一致（新增错误为空），无需再重复 parse + 全量 GetDiagnostics；
            // 基线错误集也在此刻才惰性求值。
            List<Diagnostic> newErrors;
            if (rewrittenByFile.Count == 0)
            {
                newErrors = new List<Diagnostic>();
            }
            else
            {
                baselineErrors ??= BuildErrorSet(baselineCompilation, intermediateRoot);
                newErrors = DiffErrors(
                    baselineErrors,
                    BuildCandidateCompilation(baselineCompilation, rewrittenByFile),
                    intermediateRoot);
            }

            if (newErrors.Count == 0)
            {
                acceptedStats = passStats;
                acceptedDiagnostics = passDiagnostics;
                break;
            }

            // 归属新增错误 → 唯一 struct 计划。
            var plansToRollback = new HashSet<string>(StringComparer.Ordinal);
            foreach (var error in newErrors)
            {
                if (preprocessor.TryAttributeDiagnostic(error, rewrittenByFile, out var planId)
                    && !excluded.Contains(planId)
                    && !plansToRollback.Contains(planId))
                {
                    plansToRollback.Add(planId);
                }
            }

            if (plansToRollback.Count == 0)
            {
                // 无法归属单一 plan → 回退本次运行全部改动（SV030）。
                foreach (var pair in sourceFiles)
                    tree.ResetScratchFile(pair.Source, pair.Dest);
                var first = newErrors[0];
                var span = first.Location.GetLineSpan().StartLinePosition;
                _diagnostics.Add(new StructValueSemanticsDiagnostic(
                    StructValueSemanticsSeverity.Error,
                    StructValueSemanticsDiagnosticCode.SV030,
                    "whole-tree validation failed and the new error could not be attributed to a " +
                    $"single struct plan ({first.Descriptor.Id}: {first.GetMessage()}); all changes " +
                    "from this run were rolled back.",
                    first.Location.SourceTree?.FilePath,
                    span.Line + 1,
                    span.Character + 1,
                    null));
                acceptedStats = new StructValueSemanticsStatistics();
                acceptedDiagnostics = new List<StructValueSemanticsDiagnostic>();
                success = false;
                break;
            }

            foreach (var planId in plansToRollback)
            {
                var plan = preprocessor.Plans.First(p => p.Id == planId);
                excluded.Add(planId);
                rollbackDiagnostics.Add(new StructValueSemanticsDiagnostic(
                    StructValueSemanticsSeverity.Warning,
                    StructValueSemanticsDiagnosticCode.SV031,
                    "whole-tree validation attributed a new error to this struct plan; the plan " +
                    "and all of its touched files were rolled back atomically.",
                    plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
                    (plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)
                        ?.GetLineSpan().StartLinePosition.Line ?? 0) + 1,
                    (plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)
                        ?.GetLineSpan().StartLinePosition.Character ?? 0) + 1,
                    plan.StructQualifiedName));

                // 回退该计划触碰的所有文件（可能与其他计划共享文件；下一轮全部重放）。
                foreach (var touched in plan.TouchedFiles)
                {
                    var full = Path.GetFullPath(touched);
                    var pair = sourceFiles.FirstOrDefault(p =>
                        string.Equals(p.Dest, full, StringComparison.OrdinalIgnoreCase));
                    if (pair.Source != null) tree.ResetScratchFile(pair.Source, pair.Dest);
                }
            }

            // 最后一轮仍失败：回退全部。
            if (iteration == maxIterations - 1)
            {
                foreach (var pair in sourceFiles)
                    tree.ResetScratchFile(pair.Source, pair.Dest);
                success = false;
            }
        }

        _diagnostics.AddRange(rollbackDiagnostics);
        _diagnostics.AddRange(acceptedDiagnostics);
        _statistics = acceptedStats;

        // 计划期/重写期已存在 Error（SV040/SV041/SV042/SV043/SV030 全量回退）：
        // 不存在可交付的中间树，直接判定失败，不再 restore/verify。
        if (success && !HasErrors)
        {
            var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(tree.Layout, intermediateRoot);
            if (restoreTarget != null)
            {
                var restoreExitCode = context.Restore();
                if (restoreExitCode != 0)
                {
                    success = false;
                }
                else
                {
                    success &= await context.VerifyBuildAndTestsAsync();
                    ProjectPassRunner.DeleteBuildOutputs(intermediateRoot);
                }
            }
        }

        context.Success = success && !HasErrors;

        // 设计 §7.5 residual 闸门：失败即全量回退，不允许中间目录残留。
        if (!context.Success)
        {
            DeleteIntermediateTree(intermediateRoot);
        }
    }

    /// <summary>失败路径删除中间树（MSBuild 节点可能短暂持有文件句柄，重试若干次）。</summary>
    private static void DeleteIntermediateTree(string intermediateRoot)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(intermediateRoot))
                {
                    Directory.Delete(intermediateRoot, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(200);
            }
        }
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("StructValueSemanticsPassAdapter drives the file loop itself.");

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new StructValueSemanticsDiagnostic(
            StructValueSemanticsSeverity.Error,
            StructValueSemanticsDiagnosticCode.SV060,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0, null));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new StructValueSemanticsDiagnostic(
            StructValueSemanticsSeverity.Error,
            StructValueSemanticsDiagnosticCode.SV030,
            $"dotnet build failed with exit code {exitCode} after struct-value-semantics transformation. " +
            "The defensive-clone rewrite produced code that does not compile.",
            null, 0, 0, null));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new StructValueSemanticsDiagnostic(
            StructValueSemanticsSeverity.Error,
            StructValueSemanticsDiagnosticCode.SV030,
            $"dotnet test failed with exit code {exitCode} after struct-value-semantics transformation.",
            null, 0, 0, null));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == StructValueSemanticsSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"struct-value-semantics: {_statistics.StructsPlanned} struct(s) planned, " +
           $"{_statistics.ShimsGenerated} shim(s) generated, " +
           $"{_statistics.SitesRewritten} site(s) rewritten, " +
           $"{_statistics.PlansSkipped} skipped, " +
           $"{_statistics.PlansRolledBack} rolled back";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != StructValueSemanticsSeverity.Info || verbose)
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

        var globalUsings = new StringBuilder();
        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(intermediateProjectFile, intermediateRoot);
            if (projectUsings != null) globalUsings.AppendLine(projectUsings);
        }
        if (globalUsings.Length > 0)
        {
            const string syntheticPath = "__cs2j_global_usings.g.cs";
            trees.Add(CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: syntheticPath));
        }

        return CSharpCompilation.Create(
            "struct-value-semantics-preprocess",
            trees,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    /// <summary>
    /// 候选编译：与基线的唯一差异是本次被改写文件的内容。以基线编译为底，仅对
    /// rewrittenByFile 中的树按其当前磁盘内容重新解析并 <see cref="Compilation.ReplaceSyntaxTree"/>，
    /// 其余树逐对象复用——每轮开头都把 scratch 重置为原始字节，故未改写文件的内容/路径
    /// 与基线树逐字节一致，复用在语义上等价于全量重解析；装配名/引用集/编译选项/树序
    /// 均由基线编译原样保留，等价于「用同一组树全量重建」。省去 900+ 未改写文件的
    /// 重复读取、解析与全局 using 合成。
    /// </summary>
    private static CSharpCompilation BuildCandidateCompilation(
        CSharpCompilation baseline,
        IReadOnlyDictionary<string, string> rewrittenByFile)
    {
        CSharpCompilation candidate = baseline;
        foreach (var tree in baseline.SyntaxTrees)
        {
            if (!rewrittenByFile.ContainsKey(tree.FilePath)) continue;
            var reparsed = CSharpSyntaxTree.ParseText(
                File.ReadAllText(tree.FilePath),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: tree.FilePath);
            candidate = (CSharpCompilation)candidate.ReplaceSyntaxTree(tree, reparsed);
        }
        return candidate;
    }

    /// <summary>(相对中间根路径, 错误码, 消息) 集合。</summary>
    private static HashSet<(string File, string Id, string Message)> BuildErrorSet(
        CSharpCompilation compilation,
        string intermediateRoot)
    {
        return new HashSet<(string, string, string)>(
            compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree != null)
                .Select(d => (
                    Normalize(d.Location.SourceTree!.FilePath, intermediateRoot),
                    d.Descriptor.Id,
                    d.GetMessage())));
    }

    private static List<Diagnostic> DiffErrors(
        HashSet<(string File, string Id, string Message)> baseline,
        CSharpCompilation candidate,
        string intermediateRoot)
    {
        return candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree != null
                && !baseline.Contains((
                    Normalize(d.Location.SourceTree!.FilePath, intermediateRoot),
                    d.Descriptor.Id,
                    d.GetMessage())))
            .ToList();
    }

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
