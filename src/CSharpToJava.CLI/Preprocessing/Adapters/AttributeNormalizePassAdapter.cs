using System.Text;
using CSharpToJava.Core.AttributeNormalize;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the attribute-normalize pass (C10 M1). Drives its own whole-tree
/// iteration exactly like the java-keywords pass: each round replays originals →
/// analyze plans → RewriteFile every file → recompile → diff against the baseline
/// error set → attribute new errors to plans → rollback. A loose directory without
/// any project file runs the single-file self-containment gate (CS2J1390); after
/// convergence the residual scan runs, then the intermediate tree is restored and
/// its build/tests are verified (design §4.2/§6).
/// </summary>
internal sealed class AttributeNormalizePassAdapter : ProjectPassAdapter
{
    private readonly AttributeNormalizeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly List<AttributeNormalizeDiagnostic> _diagnostics = new();
    private AttributeNormalizeStatistics _statistics = new();

    private int _inventoryTotal;
    private int _bridgeGapCount;
    private int _unknownCount;
    private int _unresolvedCount;

    public AttributeNormalizePassAdapter(
        AttributeNormalizeOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor
        => PassCatalog.ImplementedById(PassIds.AttributeNormalize);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);

    // Unlike java-keywords the intermediate tree is build/test verified (spec §3.1),
    // so bin/ output must be cleaned up afterwards just like the standard spine.
    public override bool DeleteBuildOutputsAfterRestore => true;

    public AttributeNormalizeStatistics Statistics => _statistics;
    public IReadOnlyList<AttributeNormalizeDiagnostic> Diagnostics => _diagnostics;

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

        // Preserve non-.cs assets (csproj, Directory.Build.*, ...); same rationale
        // as the java-keywords own loop. .sln stays handled by tree preparation.
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

        // Loose-directory ("single-file") mode: no project graph resolved.
        var singleFileMode = tree.Layout.ProjectFiles.Count == 0;
        var inputFileSet = sourceFiles
            .Select(p => Path.GetFullPath(p.Dest))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var baselineErrors = BuildErrorSet(
            sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Source))),
            tree.Layout, intermediateRoot);

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var nonSelfContainedPlans = new List<CallerInfoPlan>();
        var rollbackDiagnostics = new List<AttributeNormalizeDiagnostic>();
        var acceptedStats = new AttributeNormalizeStatistics();
        var acceptedDiagnostics = new List<AttributeNormalizeDiagnostic>();
        AttributeNormalizePreprocessor? lastPreprocessor = null;
        var success = true;
        const int maxIterations = 1024;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // Each round starts from the original copies to keep rewriting idempotent.
            foreach (var pair in sourceFiles)
                tree.ResetScratchFile(pair.Source, pair.Dest);

            var passStats = new AttributeNormalizeStatistics();
            var passDiagnostics = new List<AttributeNormalizeDiagnostic>();
            var preprocessor = new AttributeNormalizePreprocessor(_options);
            var compilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                tree.Layout, intermediateRoot);
            preprocessor.BindCompilation(compilation);
            preprocessor.Analyze();

            // CS2J1390 self-containment gate (design §4.2-7): loose-directory
            // ("single-file") pipelines have no project graph, so a plan may
            // only act when its declaration and all of its sites live in one
            // single input file — plans spanning multiple files (or touching
            // files outside the input set) are excluded. Computed once from
            // the deterministic input analysis. Permanent Warning, never strict.
            if (singleFileMode && iteration == 0)
            {
                foreach (var plan in preprocessor.Plans)
                {
                    var touched = plan.TouchedFiles
                        .Select(Path.GetFullPath)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var notSelfContained = touched.Count > 1
                        || touched.Any(file => !inputFileSet.Contains(file));
                    if (notSelfContained)
                    {
                        nonSelfContainedPlans.Add(plan);
                        excluded.Add(plan.Id);
                    }
                }
            }

            preprocessor.ExcludePlans(excluded);
            lastPreprocessor = preprocessor;

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
                        $"[attribute-normalize] {(result.Changed ? "transformed" : "clean (copy)")}: " +
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

            // Attribute each new error to a unique owning plan.
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
                // New error cannot be attributed to a single plan → whole-tree
                // rollback (CS2J1300, non-degradable Error).
                foreach (var pair in sourceFiles)
                    tree.ResetScratchFile(pair.Source, pair.Dest);
                var first = newErrors[0];
                var span = first.Location.GetLineSpan().StartLinePosition;
                _diagnostics.Add(new AttributeNormalizeDiagnostic(
                    AttributeNormalizeSeverity.Error,
                    AttributeNormalizeDiagnosticCode.CS2J1300,
                    "whole-tree validation failed and the new error could not be attributed to a " +
                    $"single plan ({first.Descriptor.Id}: {first.GetMessage()}); all changes " +
                    "from this run were rolled back.",
                    first.Location.SourceTree?.FilePath,
                    span.Line + 1,
                    span.Character + 1,
                    null));
                acceptedStats = new AttributeNormalizeStatistics();
                acceptedDiagnostics = new List<AttributeNormalizeDiagnostic>();
                success = false;
                break;
            }

            foreach (var planId in plansToRollback)
            {
                var plan = preprocessor.Plans.First(p => p.Id == planId);
                excluded.Add(planId);
                acceptedStats.PlansRolledBack++;
                var location = plan.DeclarationLocation
                    ?? plan.Sites.FirstOrDefault()?.SiteLocation;
                var linePosition = location?.GetLineSpan().StartLinePosition;
                rollbackDiagnostics.Add(new AttributeNormalizeDiagnostic(
                    _options.Strict
                        ? AttributeNormalizeSeverity.Error
                        : AttributeNormalizeSeverity.Warning,
                    AttributeNormalizeDiagnosticCode.CS2J1301,
                    "whole-tree validation attributed a new error to this caller plan; the plan " +
                    "and all of its touched files were rolled back atomically.",
                    location?.SourceTree?.FilePath,
                    linePosition?.Line + 1 ?? 0,
                    linePosition?.Character + 1 ?? 0,
                    plan.Id));

                // Roll back every file the plan touched; the next round replays all.
                foreach (var touched in plan.TouchedFiles)
                {
                    var full = Path.GetFullPath(touched);
                    var pair = sourceFiles.FirstOrDefault(p =>
                        string.Equals(p.Dest, full, StringComparison.OrdinalIgnoreCase));
                    if (pair.Source != null) tree.ResetScratchFile(pair.Source, pair.Dest);
                }
            }

            if (iteration == maxIterations - 1)
            {
                foreach (var pair in sourceFiles)
                    tree.ResetScratchFile(pair.Source, pair.Dest);
                success = false;
            }
        }

        // Residual reconciliation on the converged tree (design §4.4): skipped
        // entirely when the run itself rolled back.
        if (success && lastPreprocessor != null)
        {
            var finalCompilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                tree.Layout, intermediateRoot);
            var residualDiagnostics = lastPreprocessor.ResidualScan(finalCompilation, acceptedStats);
            acceptedDiagnostics.AddRange(residualDiagnostics);

            var inventory = lastPreprocessor.Inventory;
            _inventoryTotal = inventory.Occurrences.Count;
            _bridgeGapCount = inventory.CountByStatus(BridgeStatus.BridgeGap);
            _unknownCount = inventory.CountByStatus(BridgeStatus.Unknown);
            _unresolvedCount = inventory.CountByStatus(BridgeStatus.Unresolved);
        }

        // CS2J1390 self-containment warnings (emitted once; permanent Warning).
        foreach (var plan in nonSelfContainedPlans)
        {
            var location = plan.DeclarationLocation
                ?? plan.Sites.FirstOrDefault()?.SiteLocation;
            var linePosition = location?.GetLineSpan().StartLinePosition;
            _diagnostics.Add(new AttributeNormalizeDiagnostic(
                AttributeNormalizeSeverity.Warning,
                AttributeNormalizeDiagnosticCode.CS2J1390,
                "plan is not self-contained within a single input file (its declaration and call sites " +
                "span multiple files or live outside the input); skipped in single-file mode.",
                location?.SourceTree?.FilePath,
                linePosition?.Line + 1 ?? 0,
                linePosition?.Character + 1 ?? 0,
                plan.Id));
        }

        _diagnostics.AddRange(rollbackDiagnostics);
        _diagnostics.AddRange(acceptedDiagnostics);
        _statistics = acceptedStats;

        var restoreExitCode = context.Restore();
        if (restoreExitCode != 0)
        {
            success = false;
        }

        if (success)
        {
            var verified = await context.VerifyBuildAndTestsAsync();
            if (!verified) success = false;
        }

        if (DeleteBuildOutputsAfterRestore)
        {
            ProjectPassRunner.DeleteBuildOutputs(intermediateRoot);
        }

        context.Success = success;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("AttributeNormalizePassAdapter drives the file loop itself.");

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new AttributeNormalizeDiagnostic(
            AttributeNormalizeSeverity.Error,
            AttributeNormalizeDiagnosticCode.CS2J1330,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new AttributeNormalizeDiagnostic(
            AttributeNormalizeSeverity.Error,
            AttributeNormalizeDiagnosticCode.CS2J1330,
            $"dotnet build failed with exit code {exitCode} for the intermediate tree."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new AttributeNormalizeDiagnostic(
            AttributeNormalizeSeverity.Error,
            AttributeNormalizeDiagnosticCode.CS2J1331,
            $"dotnet test failed with exit code {exitCode} for the intermediate tree."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == AttributeNormalizeSeverity.Error);

    public override string BuildChainSummaryLine()
    {
        var sites = _statistics.CallerSitesFilled;
        return $"attribute-normalize: {_statistics.CallerPlansApplied} caller plan(s) applied, " +
               $"{_statistics.CallerPlansSkipped} plan(s) skipped, " +
               $"sites filled (member={CountSite(sites, CallerInfoKind.CallerMemberName)}, " +
               $"path={CountSite(sites, CallerInfoKind.CallerFilePath)}, " +
               $"line={CountSite(sites, CallerInfoKind.CallerLineNumber)}, " +
               $"expr={CountSite(sites, CallerInfoKind.CallerArgumentExpression)}), " +
               $"inventory {_inventoryTotal} attribute occurrence(s) " +
               $"({_bridgeGapCount} bridge gap(s), {_unknownCount} unknown, " +
               $"{_unresolvedCount} unresolved), " +
               $"{_statistics.PlansRolledBack} plan(s) rolled back, " +
               $"{_statistics.ResidualCount} residual(s)";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != AttributeNormalizeSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private static int CountSite(Dictionary<CallerInfoKind, int> sites, CallerInfoKind kind)
        => sites.TryGetValue(kind, out var count) ? count : 0;

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
            "attribute-normalize-preprocess",
            trees,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

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
