using System.Text;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
            File.Copy(pair.Source, pair.Dest, overwrite: true);
        }

        // 基线错误集：用原始内容、但 path 取中间树路径，保证键可直接比对。
        var baselineErrors = BuildErrorSet(
            sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Source))),
            tree.Layout, intermediateRoot);

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var rollbackDiagnostics = new List<MemberDesugarDiagnostic>();
        var acceptedStats = new MemberDesugarStatistics();
        var acceptedDiagnostics = new List<MemberDesugarDiagnostic>();
        var success = true;
        const int maxIterations = 1024;
        IReadOnlyList<MemberDesugarPlan>? lastPlans = null;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // 每轮从原始副本出发：避免对已改写文件二次驱动造成幂等问题。
            foreach (var pair in sourceFiles)
                File.Copy(pair.Source, pair.Dest, overwrite: true);

            var passStats = new MemberDesugarStatistics();
            var passDiagnostics = new List<MemberDesugarDiagnostic>();
            var preprocessor = new MemberDesugarPreprocessor(_options);
            var compilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                tree.Layout, intermediateRoot);
            preprocessor.BindCompilation(compilation);
            preprocessor.ExcludePlans(excluded);
            lastPlans = preprocessor.Plans;

            // rewrittenByFile：被改写文件 → 其原始源文件路径（值当前未参与归属，仅作"在改写集"判定）。
            var rewrittenByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sourceFiles)
            {
                var sourceCode = File.ReadAllText(pair.Dest);
                var syntaxTree = compilation.SyntaxTrees.FirstOrDefault(t =>
                    string.Equals(t.FilePath, pair.Dest, StringComparison.OrdinalIgnoreCase));
                if (syntaxTree == null) continue;

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
                        $"[member-desugar] {(result.Changed ? "transformed" : "clean (copy)")}: " +
                        $"{Path.GetRelativePath(sourceRoot, pair.Source)}");
                }
            }

            var candidateCompilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                tree.Layout, intermediateRoot);
            var newErrors = DiffErrors(baselineErrors, candidateCompilation, intermediateRoot);

            if (newErrors.Count == 0)
            {
                acceptedStats = passStats;
                acceptedDiagnostics = passDiagnostics;
                break;
            }

            // 归属新增错误 → 唯一 plan。
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
                // 无法归属单一 plan → 回退本次运行全部改动（CS2J5100）。
                foreach (var pair in sourceFiles)
                    File.Copy(pair.Source, pair.Dest, overwrite: true);
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

                // 回退该计划触碰的所有文件（可能与其他计划共享文件；下一轮全部重放）。
                foreach (var touched in plan.TouchedFiles)
                {
                    var full = Path.GetFullPath(touched);
                    var pair = sourceFiles.FirstOrDefault(p =>
                        string.Equals(p.Dest, full, StringComparison.OrdinalIgnoreCase));
                    if (pair.Source != null) File.Copy(pair.Source, pair.Dest, overwrite: true);
                }
            }

            // 最后一轮仍失败：回退全部。
            if (iteration == maxIterations - 1)
            {
                foreach (var pair in sourceFiles)
                    File.Copy(pair.Source, pair.Dest, overwrite: true);
                success = false;
            }
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
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(tree.Layout, intermediateRoot);
        if (restoreTarget != null)
        {
            context.Restore();
            await context.VerifyBuildAndTestsAsync();
            ProjectPassRunner.DeleteBuildOutputs(intermediateRoot);
        }

        context.Success = success;
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
               $"{residual} residual, {indexerResidual} indexer residual, {residualEvent} event residual";
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
            "member-desugar-preprocess",
            trees,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

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
            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!touchedFiles.Any(file => ProjectPreprocessSource.IsSameOrDescendantPath(file, projectDirectory)))
            {
                continue;
            }
            var destinationProjectFile = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, projectFile));
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

    /// <summary>(相对中间根路径, 错误码, 消息) 集合。</summary>
    private static HashSet<(string File, string Id, string Message)> BuildErrorSet(
        IEnumerable<(string Path, string Content)> files,
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot)
    {
        var compilation = BuildCompilation(files, layout, intermediateRoot);
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
