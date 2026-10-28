using System.Text;
using CSharpToJava.Core.ClosedMetadata;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the closed-metadata (C30) pass. The collector fills the closed-type
/// record set, the registration generator emits one
/// <c>_Cs2jClosedTypes.&lt;ProjectName&gt;.cs</c> per owning project (A1 declaration-driven
/// bootstrap) distributed like the generic-deepen holders, and the residual gate
/// runs. M4b: per-file rewrites insert the idempotent CM-LINK bootstrap guards at D8
/// serializer entry statements; files without a servable site stay a clean
/// byte-level copy. M4c: the gate recompiles the intermediate tree and enforces
/// §5.4 rules 1/3/4/6, with explicit skip/force overrides (--closed-metadata-overrides).
/// </summary>
internal sealed class ClosedMetadataPassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-closed-metadata-src";

    private readonly ClosedMetadataOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly ClosedMetadataPreprocessor _preprocessor = new();
    private readonly List<ClosedMetadataDiagnostic> _diagnostics = new();
    private readonly ClosedMetadataStatistics _statistics = new();
    private ClosedMetadataOverrides? _overrides;

    /// <summary>
    /// Directories (absolute, source-tree rooted — the spine compilation parses
    /// source-rooted paths) of injected cs2j support library projects present in
    /// the current tree. Closed constructions declared there are owned like
    /// framework closures (discovery-site project).
    /// </summary>
    private IReadOnlyList<string> _infrastructureDirectories = Array.Empty<string>();

    /// <summary>
    /// D6 ownership fallback map: project directory → project identity (the source
    /// tree's absolute <c>*.csproj</c> path). The collector resolves a source file's
    /// owning project by walking up to the nearest entry, so a closed construction's
    /// source-pinned ownership file can be checked for argument visibility.
    /// </summary>
    private readonly Dictionary<string, string> _projectByDirectory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Project identity → source projects it can name directly or transitively.</summary>
    private readonly Dictionary<string, HashSet<string>> _visibleProjectsByProject = new(StringComparer.OrdinalIgnoreCase);

    public ClosedMetadataPassAdapter(
        ClosedMetadataOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.ClosedMetadata);
    public override bool Verbose => _verbose;

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _infrastructureDirectories = tree.Layout.ProjectFiles
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && IsInjectedSupportLibraryProject(file))
            .Select(file => Path.GetFullPath(Path.IsPathRooted(file)
                ? file
                : Path.Combine(tree.SourceRoot, file)))
            .Select(projectPath => Path.GetDirectoryName(projectPath)!)
            .ToList();

        BuildProjectVisibility(tree);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the project identity map and the transitive <c>ProjectReference</c>
    /// closure used by the D6 ownership fallback. References are read from the
    /// intermediate tree (its csproj files carry the pass-injected support-library
    /// references) and mapped back to the source-tree project identities the
    /// collector's ownership paths resolve to.
    /// </summary>
    private void BuildProjectVisibility(PassTreeContext tree)
    {
        _projectByDirectory.Clear();
        _visibleProjectsByProject.Clear();

        var sourceProjects = tree.Layout.ProjectFiles
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetFullPath(Path.IsPathRooted(file)
                ? file
                : Path.Combine(tree.SourceRoot, file)))
            .ToList();

        var intermediateBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceProject in sourceProjects)
        {
            var intermediateProject = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, sourceProject));
            intermediateBySource[sourceProject] = intermediateProject;
            _projectByDirectory[NormalizeDirectory(Path.GetDirectoryName(intermediateProject)!)] = sourceProject;
        }

        foreach (var (sourceProject, intermediateProject) in intermediateBySource)
        {
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { intermediateProject };
            var pending = new Queue<string>();
            pending.Enqueue(intermediateProject);
            while (pending.Count > 0)
            {
                foreach (var reference in ProjectCompileSet.ReadProjectReferences(pending.Dequeue()))
                {
                    var referenceDirectory = Path.GetDirectoryName(Path.GetFullPath(reference));
                    if (referenceDirectory is null
                        || !_projectByDirectory.TryGetValue(NormalizeDirectory(referenceDirectory), out var referencedProject)
                        || !visible.Add(referencedProject))
                    {
                        continue;
                    }

                    if (intermediateBySource.TryGetValue(referencedProject, out var referencedIntermediate)
                        && visited.Add(referencedIntermediate))
                    {
                        pending.Enqueue(referencedIntermediate);
                    }
                }
            }

            _visibleProjectsByProject[sourceProject] = visible;
        }
    }

    private static string NormalizeDirectory(string directory) =>
        directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Maps a source file to its owning project identity by walking up to the
    /// nearest project directory (layout-known first, then on-disk <c>*.csproj</c>).
    /// </summary>
    private string? ResolveProjectIdentity(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (directory is not null)
        {
            var normalized = NormalizeDirectory(directory);
            if (_projectByDirectory.TryGetValue(normalized, out var project))
            {
                return project;
            }

            var projectFile = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault()
                : null;
            if (projectFile is not null)
            {
                return Path.GetFullPath(projectFile);
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private bool IsProjectVisible(string from, string to)
        => string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
            || (_visibleProjectsByProject.TryGetValue(from, out var visible) && visible.Contains(to));

    /// <summary>
    /// Injected support projects can only exist under their reserved csproj
    /// names (their injecting passes reject same-named user projects up front),
    /// so filename recognition is sufficient regardless of which passes ran.
    /// </summary>
    private static bool IsInjectedSupportLibraryProject(string projectFilePath) =>
        Cs2jRefOutInProject.IsCs2jRefOutInProject(projectFilePath)
        || Cs2jArrayProject.IsCs2jArrayProject(projectFilePath)
        || Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(projectFilePath)
        || Cs2jPreludeProject.IsCs2jPreludeProject(projectFilePath);

    private bool IsInfrastructureSource(string sourceFilePath)
    {
        if (_infrastructureDirectories.Count == 0)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(sourceFilePath);
        return _infrastructureDirectories.Any(directory =>
            fullPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(directory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override string CompilationName => "cs2j-closed-metadata-preprocess";

    /// <summary>
    /// ImplicitUsings=enable projects rely on SDK-generated GlobalUsings.g.cs;
    /// the spine compilation parses sources directly, so without synthesis the
    /// collector binds names differently from the real build (e.g. an unqualified
    /// <c>IEnumerable&lt;int&gt;</c> can bind to the non-generic
    /// <c>System.Collections.IEnumerable</c> and poison the generated
    /// registration file). Mirrors PartialMergePassAdapter.
    /// </summary>
    public override IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
    {
        var globalUsings = new StringBuilder();
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.Layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(
                intermediateProjectFile, tree.IntermediateRoot);
            if (projectUsings != null)
            {
                globalUsings.AppendLine(projectUsings);
            }
        }

        if (globalUsings.Length == 0)
        {
            return Array.Empty<SyntaxTree>();
        }

        return new[]
        {
            CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: "__cs2j_global_usings.g.cs"),
        };
    }

    public ClosedMetadataStatistics Statistics => _statistics;
    public IReadOnlyList<ClosedMetadataDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        // Chain hard dependency (design §4.4): registration must read the final
        // post-C02 symbol shapes, so generic-deepen has to be enabled in the
        // same run — otherwise fail loudly with CS2J1205 instead of silently
        // registering against intermediate shapes.
        if (_options.EnabledPassIds is not { } enabledIds || !enabledIds.Contains(PassIds.GenericDeepen))
        {
            _diagnostics.Add(new ClosedMetadataDiagnostic(
                ClosedMetadataSeverity.Error,
                ClosedMetadataRule.ChainPosition,
                ClosedMetadataCodes.GenericDeepenRequired,
                "closed-metadata requires generic-deepen to be enabled in the same run " +
                "(hard chain dependency: registration reads the final post-generic-deepen " +
                "symbol shapes); enable both passes or disable closed-metadata."));
            return Task.CompletedTask;
        }

        LoadOverrides();

        _preprocessor.BindCompilation(compilation);
        _preprocessor.HolderNameResolver = ResolveHolderName;
        _preprocessor.Analyze(_options with
        {
            IsInfrastructureSource = _infrastructureDirectories.Count == 0
                ? null
                : IsInfrastructureSource,
            ProjectIdentityResolver = _projectByDirectory.Count == 0
                ? null
                : ResolveProjectIdentity,
            IsProjectVisible = _visibleProjectsByProject.Count == 0
                ? null
                : IsProjectVisible,
        });
        _statistics.Merge(_preprocessor.Analysis.Statistics);
        if (_verbose)
        {
            Console.WriteLine(
                $"[closed-metadata] analysis: {_preprocessor.Analysis.ClosedTypes.Count} closed type(s) registered, " +
                $"{_preprocessor.Analysis.Statistics.DynamicSitesSkipped} dynamic site(s) skipped");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Opt-in: the per-file rewrite is a pure function of the (immutable post-analysis)
    /// <see cref="ClosedMetadataPreprocessor"/> state — <c>_compilation</c>,
    /// <c>_analysis</c> and the holder resolver are all fixed before the loop; each
    /// call builds its own <c>ClosedMetadataConsumptionLinker</c> and never mutates
    /// shared state — so the spine may run the read + rewrite step in parallel. The
    /// one shared cache (<see cref="ResolveHolderName"/>'s directory→holder map) is
    /// guarded by a lock; every accumulation (statistics, diagnostics, verbose lines)
    /// is deferred to <see cref="MergeFilePureResult"/>, which the runner calls serially
    /// in source-file order — keeping summary counts and diagnostic order identical to
    /// the sequential path.
    /// </summary>
    public override bool SupportsParallelFileRewrite => true;

    public override async Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Single source of truth for both spines: rewrite purely, then merge.
        var pure = await RewriteFilePureAsync(context);
        MergeFilePureResult(pure);
        return pure.Rewrite;
    }

    public override Task<PassFilePureResult> RewriteFilePureAsync(PassFileContext context)
    {
        if (context.SemanticModel is null)
        {
            return Task.FromResult(new PassFilePureResult
            {
                Context = context,
                Rewrite = null,
                PassState = null,
            });
        }

        // CM-LINK (M4b): insert the idempotent bootstrap guards in front of D8
        // serializer entry statements; files without a servable site stay a clean
        // byte-level copy (BOM and line endings preserved verbatim).
        var selfHolderName = ResolveHolderName(context.SourceFilePath);
        var result = _preprocessor.RewriteFile(context.SemanticModel, selfHolderName);
        PassFileRewrite? rewrite = result is { Changed: true }
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(new PassFilePureResult
        {
            Context = context,
            Rewrite = rewrite,
            PassState = result,
        });
    }

    public override void MergeFilePureResult(PassFilePureResult pure)
    {
        var context = pure.Context;
        var result = pure.PassState as ClosedMetadataFileResult;
        if (result is null || !result.Changed)
        {
            if (_verbose)
            {
                Console.WriteLine($"[closed-metadata] clean (copy): {context.RelativePath}");
            }

            return;
        }

        _statistics.Merge(result.Statistics);
        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[closed-metadata] linked {result.Statistics.ConsumerSitesLinked} consumer site(s): {context.RelativePath}");
        }
    }

    /// <summary>
    /// Maps a source file to its owning project's generated holder type name by
    /// walking up to the nearest <c>*.csproj</c>. The spine compilation parses source
    /// trees from the source root, so record ownership paths resolve directly there;
    /// the source and intermediate trees share the same project directory layout.
    /// Unmapped paths (no project on disk) resolve to null and simply stay unguarded.
    /// </summary>
    private readonly Dictionary<string, string?> _holderByDirectory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards <see cref="_holderByDirectory"/>: the resolver is invoked both from the
    /// single-threaded analysis phase and (when <see cref="SupportsParallelFileRewrite"/>
    /// is on) concurrently from the parallel per-file rewrite phase.
    /// </summary>
    private readonly object _holderLock = new();

    private string? ResolveHolderName(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        lock (_holderLock)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            while (directory is not null)
            {
                if (_holderByDirectory.TryGetValue(directory, out var cached))
                {
                    return cached;
                }

                var projectFile = Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault();
                if (projectFile is not null)
                {
                    var holder = ClosedMetadataRegistrationGenerator.HolderTypeName(
                        Path.GetFileNameWithoutExtension(projectFile));
                    _holderByDirectory[directory] = holder;
                    return holder;
                }

                _holderByDirectory[directory] = null;
                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        DistributeRegistrationFiles(tree);
        RunResidualGate(tree);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registration-file ownership = declaring project (design D6): the record's
    /// ownership file is the closed type's own source declaration (or the discovery
    /// site for framework types), so the project compiling that file gets the
    /// registration file; every other usage reaches the closed types through project
    /// references.
    /// </summary>
    private void DistributeRegistrationFiles(PassTreeContext tree)
    {
        var records = _preprocessor.Analysis.ClosedTypes;
        if (records.Count == 0)
        {
            return;
        }

        var layout = tree.Layout;
        var intermediateRoot = tree.IntermediateRoot;
        var sourceRoot = tree.SourceRoot;
        var exclusionInfo = tree.Exclusion;

        var projectFiles = layout.ProjectFiles
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();

        var recordsByProject = new Dictionary<string, IReadOnlyList<ClosedTypeRecord>>(StringComparer.OrdinalIgnoreCase);
        var projectFilePathByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            var compiled = ProjectCompileSet.ComputeCompiledFiles(intermediateProjectFile, intermediateRoot);
            if (compiled == null)
            {
                continue;
            }

            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record.DeclaringFilePath))
                {
                    continue;
                }

                var recordPath = GeneratedFileWiring.ToIntermediatePath(
                    record.DeclaringFilePath, sourceRoot, intermediateRoot);
                if (!compiled.Contains(recordPath))
                {
                    continue;
                }

                var projectName = Path.GetFileNameWithoutExtension(projectFile);
                if (recordsByProject.TryGetValue(projectName, out var owned))
                {
                    recordsByProject[projectName] = owned.Append(record).ToList();
                }
                else
                {
                    recordsByProject[projectName] = [record];
                    projectFilePathByProject[projectName] = projectFile;
                }
            }
        }

        if (recordsByProject.Count == 0)
        {
            _diagnostics.Add(new ClosedMetadataDiagnostic(
                ClosedMetadataSeverity.Warning,
                ClosedMetadataRule.Enumeration,
                ClosedMetadataCodes.PlanNotSelfContained,
                $"{records.Count} closed type(s) could not be mapped to a declaring project; no registration file was generated.",
                sourceRoot));
            return;
        }

        var files = _preprocessor.GenerateRegistrationFiles(recordsByProject);
        foreach (var file in files)
        {
            var projectFile = projectFilePathByProject[file.ProjectName];
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            var rootPath = Path.Combine(intermediateRoot, file.FileName);
            File.WriteAllText(rootPath, file.Content, new UTF8Encoding(false));
            GeneratedFileWiring.EnsureCompileItem(
                intermediateProjectFile,
                file.FileName,
                Path.GetDirectoryName(intermediateProjectFile)!,
                rootPath);
            _statistics.RegisterFilesGenerated++;

            if (_verbose)
            {
                Console.WriteLine($"[closed-metadata] {file.ProjectName}: {file.FileName} generated");
            }
        }
    }

    /// <summary>
    /// §5.4 gate: rules 2/5 content scan of the generated registration files plus
    /// rules 1/3/4/6 over a recompilation of the whole rewritten intermediate tree
    /// (same parse/reference/options shape as the spine compilation).
    /// </summary>
    private void RunResidualGate(PassTreeContext tree)
    {
        var files = _preprocessor.GeneratedFileSpecs;
        if (files.Count > 0)
        {
            MergeResidual(_preprocessor.ResidualScan(files));
        }

        var parseOptions = CreateParseOptions(tree);
        var rewrittenTrees = new List<Microsoft.CodeAnalysis.SyntaxTree>();
        foreach (var file in Directory.EnumerateFiles(tree.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rewrittenTrees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, path: file));
        }

        if (rewrittenTrees.Count == 0)
        {
            return;
        }

        var compilation = CSharpCompilation.Create(
            CompilationName + "-residual",
            rewrittenTrees, GetMetadataReferences(), CreateCompilationOptions());
        MergeResidual(_preprocessor.ResidualScan(compilation, _overrides));
    }

    private void MergeResidual(ClosedMetadataResidualResult residual)
    {
        foreach (var (rule, count) in residual.Counts)
        {
            _statistics.ResidualsByRule.TryGetValue(rule, out var existing);
            _statistics.ResidualsByRule[rule] = existing + count;
        }

        _diagnostics.AddRange(residual.Diagnostics);
    }

    /// <summary>Loads the explicit skip/force overrides JSON (design §6.3); a bad file warns and runs without overrides.</summary>
    private void LoadOverrides()
    {
        if (string.IsNullOrEmpty(_options.OverridesPath))
        {
            return;
        }

        try
        {
            _overrides = ClosedMetadataOverrides.Load(_options.OverridesPath);
            if (_verbose)
            {
                Console.WriteLine(
                    $"[closed-metadata] overrides loaded: {_overrides.SkipEntries.Count} skip, " +
                    $"{_overrides.ForceEntries.Count} force entries");
            }
        }
        catch (Exception ex) when (ex is ClosedMetadataOverridesException or IOException or System.Text.Json.JsonException)
        {
            _diagnostics.Add(new ClosedMetadataDiagnostic(
                ClosedMetadataSeverity.Warning,
                ClosedMetadataRule.Residual,
                ClosedMetadataCodes.ResidualFound,
                $"Failed to load closed-metadata overrides from {_options.OverridesPath}: {ex.Message}; " +
                "running the residual gate without overrides."));
        }
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ClosedMetadataDiagnostic(
            ClosedMetadataSeverity.Warning,
            ClosedMetadataRule.Infrastructure,
            ClosedMetadataCodes.RestoreOrBuildFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ClosedMetadataDiagnostic(
            ClosedMetadataSeverity.Error,
            ClosedMetadataRule.Infrastructure,
            ClosedMetadataCodes.RestoreOrBuildFailed,
            $"dotnet build failed with exit code {exitCode} after closed-metadata preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ClosedMetadataDiagnostic(
            ClosedMetadataSeverity.Error,
            ClosedMetadataRule.Infrastructure,
            ClosedMetadataCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after closed-metadata preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ClosedMetadataSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != ClosedMetadataSeverity.Info || verbose)
            .Select(d => d.ToString())
            .ToList();
}
