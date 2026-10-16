using System.Text;
using System.Xml.Linq;
using CSharpToJava.Core.GenericPreprocess;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the generic-deepen (C02) pass: whole-tree analyze once, per-file
/// rewrite, generated holder files distributed after all files (Tuple pattern).
/// </summary>
internal sealed class GenericDeepenPassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-generic-deepen-src";

    private readonly GenericDeepenOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly GenericDeepenPreprocessor _preprocessor = new();
    private readonly List<GenericDeepenDiagnostic> _diagnostics = new();
    private readonly GenericDeepenStatistics _statistics = new();

    /// <summary>Normalized intermediate paths of the files this run actually rewrote. Used
    /// by the residual gate's necessary-condition gate (a rewritten file always runs the
    /// full per-tree rules even if it declares no type parameter).</summary>
    private readonly HashSet<string> _rewrittenPaths = new(StringComparer.OrdinalIgnoreCase);

    public GenericDeepenPassAdapter(
        GenericDeepenOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.GenericDeepen);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public GenericDeepenStatistics Statistics => _statistics;
    public IReadOnlyList<GenericDeepenDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        _preprocessor.BindCompilation(compilation);
        var analysis = _preprocessor.Analyze(_options);
        // Analysis-phase counters exist once per tree (nested-closure propagations,
        // expression-tree skips, plan skips); the per-file rewrites add the rest.
        // Diagnostics are delivered per file via RewriteFileAsync (by relative path).
        _statistics.Merge(analysis.Statistics);
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.RewriteFile(context.SemanticModel!);
        _statistics.Merge(result.Statistics);
        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (result.Changed)
        {
            _rewrittenPaths.Add(Path.GetFullPath(context.DestinationFilePath));
        }

        if (_verbose)
        {
            Console.WriteLine($"[generic-deepen] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        DistributeHolderFiles(tree);
        RunResidualScan(tree);
        return Task.CompletedTask;
    }

    /// <summary>Post-rewrite gate (spec §4.5): recompile the intermediate tree and count shapes C02 must have removed.</summary>
    private void RunResidualScan(PassTreeContext tree)
    {
        if (!_options.RunResidualScan)
        {
            return;
        }

        var parseOptions = CreateParseOptions(tree);
        var trees = new List<SyntaxTree>();

        // Parse the rewritten tree back in, in enumeration order. Parsing is pure, so it
        // runs in parallel and the trees are appended in the original order afterwards —
        // the gate's finding order (and therefore its diagnostics) is unchanged.
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(tree.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(file);
        }

        var parsed = new SyntaxTree[files.Count];
        System.Threading.Tasks.Parallel.For(0, files.Count, i =>
        {
            parsed[i] = CSharpSyntaxTree.ParseText(File.ReadAllText(files[i]), parseOptions, path: files[i]);
        });
        trees.AddRange(parsed);

        if (trees.Count == 0)
        {
            return;
        }

        var compilation = CSharpCompilation.Create(
            CompilationName ?? "cs2j-generic-deepen-residual",
            trees, GetMetadataReferences(), CreateCompilationOptions());

        // The analysis reports active trees by their source-tree path; the rewritten
        // trees carry the intermediate path. Translate by relative path so the gate's
        // necessary-condition gate lines up (identity when source == intermediate).
        var activeTreePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourcePath in _preprocessor.Analysis.ActiveFilePaths)
        {
            var relative = Path.GetRelativePath(tree.SourceRoot, sourcePath);
            activeTreePaths.Add(Path.GetFullPath(Path.Combine(tree.IntermediateRoot, relative)));
        }

        activeTreePaths.UnionWith(_rewrittenPaths);
        var report = _preprocessor.ResidualScan(compilation, activeTreePaths);
        foreach (var (rule, count) in report.Counts)
        {
            _statistics.ResidualsByRule.TryGetValue(rule, out var existing);
            _statistics.ResidualsByRule[rule] = existing + count;
        }

        foreach (var diagnostic in report.Diagnostics)
        {
            _diagnostics.Add(diagnostic.FilePath == null
                ? diagnostic with { FilePath = tree.IntermediateRoot }
                : diagnostic);
        }
    }

    /// <summary>
    /// Holder ownership = declaring project: the project that compiles the file
    /// declaring the field gets the holder class; every other usage reaches it via
    /// project references. Tuple's cross-project reachability analysis is not needed.
    /// </summary>
    private void DistributeHolderFiles(PassTreeContext tree)
    {
        var staticFields = _preprocessor.Analysis.StaticFields;
        if (staticFields.Count == 0)
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

        var plansByProject = new Dictionary<string, List<StaticFieldPlan>>(StringComparer.OrdinalIgnoreCase);
        var unmappedPlans = new List<StaticFieldPlan>();
        foreach (var projectFile in projectFiles)
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            var compiled = ProjectCompileSet.ComputeCompiledFiles(intermediateProjectFile, intermediateRoot);
            if (compiled == null)
            {
                continue;
            }

            var owned = staticFields
                .Where(plan => compiled.Contains(ToIntermediatePath(plan.FilePath, sourceRoot, intermediateRoot)))
                .ToList();
            if (owned.Count > 0)
            {
                plansByProject[projectFile] = owned;
            }
        }

        foreach (var plan in staticFields)
        {
            if (!plansByProject.Values.Any(list => list.Contains(plan)))
            {
                unmappedPlans.Add(plan);
            }
        }

        foreach (var (projectFile, plans) in plansByProject)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var fileName = $"{GenericDeepenSyntaxFacts.GeneratedFileStem}.{projectName}.cs";
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));

            var index = 0;
            foreach (var content in GenericDeepenHolderGenerator.Generate(plans))
            {
                var rootPath = Path.Combine(intermediateRoot, index == 0 ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)}.{index}.cs");
                File.WriteAllText(rootPath, content, new UTF8Encoding(false));
                GeneratedFileWiring.EnsureCompileItem(
                    intermediateProjectFile,
                    Path.GetFileName(rootPath),
                    Path.GetDirectoryName(intermediateProjectFile)!,
                    rootPath);
                index++;
                _statistics.StaticFieldHoldersGenerated++;
            }

            _statistics.StaticFieldAccessorsGenerated += plans.Sum(
                plan => plan.IsSingletonForm || !plan.NeedsSetter ? 1 : 2);

            if (_verbose)
            {
                Console.WriteLine($"[generic-deepen] {projectName}: {plans.Count} static generic field(s) -> {fileName}");
            }
        }

        foreach (var plan in unmappedPlans)
        {
            _diagnostics.Add(new GenericDeepenDiagnostic(
                GenericDeepenSeverity.Warning, GenericDeepenRule.StaticField, GenericDeepenCodes.CallSiteOutsideCompilation,
                $"Static generic field '{plan.FieldName}' could not be mapped to a declaring project; the holder was not generated and the field declaration was already removed — add the file to a project or disable the pass.",
                plan.FilePath));
        }
    }

    private static string ToIntermediatePath(string filePath, string sourceRoot, string intermediateRoot)
        => GeneratedFileWiring.ToIntermediatePath(filePath, sourceRoot, intermediateRoot);

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Warning,
            GenericDeepenRule.Infrastructure,
            GenericDeepenCodes.RestoreOrBuildFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Error,
            GenericDeepenRule.Infrastructure,
            GenericDeepenCodes.RestoreOrBuildFailed,
            $"dotnet build failed with exit code {exitCode} after generic-deepen preprocessing; the rewrite produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Error,
            GenericDeepenRule.Infrastructure,
            GenericDeepenCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after generic-deepen preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == GenericDeepenSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != GenericDeepenSeverity.Info || verbose)
            .Select(d => d.ToString())
            .ToList();
}
