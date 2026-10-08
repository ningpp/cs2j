using System.Text;
using CSharpToJava.Core.ClosedMetadata;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the closed-metadata (C30) pass. M3 (design §10): the collector fills
/// the closed-type record set, the registration generator emits one
/// <c>_Cs2jClosedTypes.&lt;ProjectName&gt;.cs</c> per owning project (A1 declaration-driven
/// bootstrap) distributed like the generic-deepen holders, and the minimal residual
/// gate runs. Per-file rewrites stay a clean byte-level copy (consumer linking is M4).
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

        _preprocessor.BindCompilation(compilation);
        _preprocessor.Analyze(_options);
        _statistics.Merge(_preprocessor.Analysis.Statistics);
        if (_verbose)
        {
            Console.WriteLine(
                $"[closed-metadata] analysis: {_preprocessor.Analysis.ClosedTypes.Count} closed type(s) registered, " +
                $"{_preprocessor.Analysis.Statistics.DynamicSitesSkipped} dynamic site(s) skipped");
        }

        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // M3: zero rewrites — the runner performs the clean byte-level copy for every
        // file (BOM and line endings preserved verbatim). Consumer linking is M4.
        if (_verbose)
        {
            Console.WriteLine($"[closed-metadata] clean (copy): {context.RelativePath}");
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        DistributeRegistrationFiles(tree);
        RunResidualGate();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registration-file ownership = declaring project (design D6): the project that
    /// compiles the file containing the discovery site gets the registration file;
    /// every other usage reaches the closed types through project references.
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

    /// <summary>M3 minimal residual gate: duplicate/conflicting registration keys in the generated files (§5.4 rules 2/5).</summary>
    private void RunResidualGate()
    {
        var files = _preprocessor.GeneratedFileSpecs;
        if (files.Count == 0)
        {
            return;
        }

        var residual = _preprocessor.ResidualScan(files);
        foreach (var (rule, count) in residual.Counts)
        {
            _statistics.ResidualsByRule[rule] = count;
        }

        _diagnostics.AddRange(residual.Diagnostics);
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
