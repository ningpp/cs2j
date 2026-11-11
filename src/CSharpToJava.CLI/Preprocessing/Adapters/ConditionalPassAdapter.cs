using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpToJava.Core.ConditionalDirectiveStrip;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the conditional-directive strip pass. Text-only; resolves the
/// preprocessor symbol set per file via <see cref="ProjectSymbolResolver"/>.
/// Preserves the string-diagnostic shape ("[Error]"/"[Warning]" lines) of the
/// former ProjectConditionalDirectivePreprocessor facade.
/// </summary>
internal sealed class ConditionalPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly string _configuration;
    private readonly string? _tfm;
    private readonly bool _removeRegions;
    private readonly string? _sourceRoot;

    private readonly List<string> _diagnostics = new();

    /// <summary>
    /// Per-file nearest-project lookup cache. Files in the same directory can
    /// belong to different projects (a directory may host several csproj), so
    /// the owner must be resolved per file; parallel rewrite shares this map.
    /// </summary>
    private readonly ConcurrentDictionary<string, string?> _projectByFilePath =
        new(StringComparer.OrdinalIgnoreCase);

    private int _filesTransformed;
    private int _filesSkippedClean;
    private int _directivesRemoved;
    private int _disabledBranchesRemoved;
    private int _materializedCopies;

    private static readonly Regex ConditionalDirectivePattern =
        new(@"^\s*#\s*(if|elif)\b", RegexOptions.Multiline | RegexOptions.Compiled);

    public ConditionalPassAdapter(
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        string configuration,
        string? tfm,
        bool removeRegions,
        string? sourceRoot = null)
    {
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _configuration = configuration;
        _tfm = tfm;
        _removeRegions = removeRegions;
        _sourceRoot = sourceRoot;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.Conditional);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;

    public int FilesTransformed => _filesTransformed;
    public int FilesSkippedClean => _filesSkippedClean;
    public int DirectivesRemoved => _directivesRemoved;
    public int DisabledBranchesRemoved => _disabledBranchesRemoved;
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>
    /// Opt-in: the per-file rewrite is a pure function — <see cref="ProjectSymbolResolver"/>
    /// is static and every file gets a fresh <see cref="ConditionalDirectiveStripper"/>,
    /// so the spine may run the read + rewrite step in parallel. The per-file diagnostic
    /// lines (symbol warnings first, then strip diagnostics, in historical order) and
    /// counters travel through <see cref="PassFilePureResult.PassState"/> and are applied
    /// by <see cref="MergeFilePureResult"/>, which the runner calls serially in
    /// source-file order — keeping the summary counters, diagnostic order and verbose
    /// lines identical to the sequential path.
    /// </summary>
    public override bool SupportsParallelFileRewrite => true;

    /// <summary>Per-file side effects carried out of the parallel phase.</summary>
    private sealed record ConditionalFileState(
        List<string> DiagnosticLines,
        bool Changed,
        int DirectivesRemoved,
        int DisabledBranchesRemoved);

    public override async Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Single source of truth for both spines: rewrite purely, then merge.
        var pure = await RewriteFilePureAsync(context);
        MergeFilePureResult(pure);
        return pure.Rewrite;
    }

    public override Task<PassFilePureResult> RewriteFilePureAsync(PassFileContext context)
    {
        // Without an explicit --tfm, an unresolvable project TFM (e.g.
        // $(NetCoreAppCurrent)) is upgraded through MSBuild evaluation once per
        // project, then passed in so ResolveForFile never re-reads the XML and
        // never emits "skipped unresolvable TFM value".
        var resolvedTfm = string.IsNullOrWhiteSpace(_tfm)
            ? ResolveProjectTfm(context.SourceFilePath)
            : null;

        var symbols = ProjectSymbolResolver.ResolveForFile(
            context.SourceFilePath,
            _configuration,
            _tfm,
            resolvedTfm: resolvedTfm,
            sourceRoot: _sourceRoot,
            projectTfmResolver: ResolveTfmForProject,
            sourceCode: context.SourceCode);

        var diagnosticLines = new List<string>(symbols.Warnings.Count);
        foreach (var warning in symbols.Warnings)
        {
            diagnosticLines.Add($"[Warning] {context.RelativePath}:0: {warning}");
        }

        var stripOptions = new ConditionalDirectiveStripOptions
        {
            RemoveRegions = _removeRegions,
            Symbols = symbols.Symbols,
        };

        var result = new ConditionalDirectiveStripper().Strip(context.SourceCode, stripOptions);

        foreach (var diagnostic in result.Diagnostics)
        {
            var severity = diagnostic.Severity == ConditionalDirectiveSeverity.Error ? "Error" : "Warning";
            diagnosticLines.Add(
                $"[{severity}] {context.RelativePath}:{diagnostic.Line ?? 0}: {diagnostic.Message}");
        }

        PassFileRewrite? rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(new PassFilePureResult
        {
            Context = context,
            Rewrite = rewrite,
            PassState = new ConditionalFileState(
                diagnosticLines,
                result.Changed,
                result.Statistics.DirectivesRemoved,
                result.Statistics.DisabledBranchesRemoved),
        });
    }

    private string? ResolveTfmForProject(string projectPath)
        => ResolvedProjectTfm.Resolve(projectPath, _configuration);

    private string? ResolveProjectTfm(string filePath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            return null;
        }

        var projectPath = _projectByFilePath.GetOrAdd(
            fullPath, static path => ProjectSymbolResolver.FindNearestProjectForFile(path));
        return projectPath == null
            ? null
            : ResolvedProjectTfm.Resolve(projectPath, _configuration);
    }

    public override void MergeFilePureResult(PassFilePureResult pure)
    {
        var context = pure.Context;
        var state = (ConditionalFileState)pure.PassState!;

        foreach (var line in state.DiagnosticLines)
        {
            _diagnostics.Add(line);
        }

        if (state.Changed)
        {
            _filesTransformed++;
            _directivesRemoved += state.DirectivesRemoved;
            _disabledBranchesRemoved += state.DisabledBranchesRemoved;

            if (_verbose)
            {
                Console.WriteLine(
                    $"[conditional] transformed: {context.RelativePath} " +
                    $"({state.DirectivesRemoved} directives, " +
                    $"{state.DisabledBranchesRemoved} disabled blocks)");
            }

            return;
        }

        _filesSkippedClean++;
        if (_verbose)
        {
            Console.WriteLine($"[conditional] clean (copy): {context.RelativePath}");
        }
    }

    /// <summary>
    /// After the shared tree is stripped, a few shared files still cannot serve
    /// every owner with one merged form: a public/internal visibility split
    /// (CS0433 once a consumer references both assemblies) or a branch one owner
    /// cannot compile (CS0117 against a resource it does not define). For each
    /// such file, every owner whose own symbol set yields a different stripped
    /// form gets a private copy, and its csproj is redirected to compile that
    /// copy instead of the shared file. Owners that agree with the merged form
    /// keep using the shared file, so the union remains the default.
    /// </summary>
    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        if (tree.InPlace)
        {
            return;
        }

        var index = ProjectCompileIndex.ForRoot(tree.SourceRoot, _configuration);
        if (index.ProjectCount == 0)
        {
            return;
        }

        foreach (var sourceFile in tree.CsFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            if (!ConditionalDirectivePattern.IsMatch(sourceCode))
            {
                continue;
            }

            var ownerSets = ProjectSymbolResolver.ResolveOwnerSymbolSets(
                sourceFile, tree.SourceRoot, _configuration, ResolveTfmForProject);
            if (ownerSets.Count < 2)
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(tree.SourceRoot, sourceFile);
            var sharedDestination = Path.Combine(tree.IntermediateRoot, relativePath);
            if (!File.Exists(sharedDestination))
            {
                continue;
            }

            var sharedOutput = await File.ReadAllTextAsync(sharedDestination);

            foreach (var (owner, symbols) in ownerSets)
            {
                var ownerResult = new ConditionalDirectiveStripper().Strip(
                    sourceCode,
                    new ConditionalDirectiveStripOptions { RemoveRegions = _removeRegions, Symbols = symbols });
                if (!ownerResult.Changed
                    || ownerResult.Diagnostics.Any(d => d.Severity == ConditionalDirectiveSeverity.Error)
                    || string.Equals(ownerResult.OutputCode, sharedOutput, StringComparison.Ordinal))
                {
                    continue;
                }

                await MaterializeOwnerCopyAsync(tree, owner, relativePath, ownerResult.OutputCode);
            }
        }
    }

    private async Task MaterializeOwnerCopyAsync(
        PassTreeContext tree, string ownerProject, string relativePath, string ownerOutput)
    {
        var ownerRelative = Path.GetRelativePath(tree.SourceRoot, ownerProject);
        var destinationOwnerProject = Path.Combine(tree.IntermediateRoot, ownerRelative);
        var ownerDirectory = Path.GetDirectoryName(destinationOwnerProject)!;

        var copyDestination = Path.Combine(ownerDirectory, ".cs2j-conditional", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(copyDestination)!);
        await File.WriteAllTextAsync(copyDestination, ownerOutput, new UTF8Encoding(false));

        var sharedDestination = Path.Combine(tree.IntermediateRoot, relativePath);
        var removeSpec = ToProjectRelativePath(ownerDirectory, sharedDestination);
        var includeSpec = ToProjectRelativePath(ownerDirectory, copyDestination);
        AddCompileRedirect(destinationOwnerProject, removeSpec, includeSpec);

        _materializedCopies++;
        _diagnostics.Add(
            $"[Warning] {relativePath}:0: materialized a private copy for {Path.GetFileName(ownerProject)} " +
            "(its conditional branches differ from the merged shared form)");
    }

    private static string ToProjectRelativePath(string projectDirectory, string target)
        => Path.GetRelativePath(projectDirectory, target).Replace('\\', '/');

    /// <summary>
    /// Appends a <c>Compile Remove</c> for the shared file and a <c>Compile
    /// Include</c> for the private copy. The Remove matches both explicit
    /// includes and glob-expanded items because it is evaluated against the
    /// accumulated item list at the end of the project. DefaultItemExcludes keeps
    /// default compile items from double-including the copy.
    /// </summary>
    private static void AddCompileRedirect(string csprojPath, string removeSpec, string includeSpec)
    {
        var document = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        var root = document.Root!;
        var ns = root.Name.Namespace;

        var hasExclude = root.Descendants(ns + "DefaultItemExcludes")
            .Any(element => element.Value.Contains(".cs2j-conditional", StringComparison.Ordinal));
        if (!hasExclude)
        {
            root.Add(new XElement(ns + "PropertyGroup",
                new XElement(ns + "DefaultItemExcludes", "$(DefaultItemExcludes);.cs2j-conditional/**")));
        }

        root.Add(new XElement(ns + "ItemGroup",
            new XElement(ns + "Compile", new XAttribute("Remove", removeSpec)),
            new XElement(ns + "Compile", new XAttribute("Include", includeSpec))));
        document.Save(csprojPath);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(
            $"[Warning] dotnet restore failed with exit code {exitCode} for {restoreTarget}.");

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add($"[Error] dotnet build failed with exit code {exitCode} after conditional preprocessing.");

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add($"[Error] dotnet test failed with exit code {exitCode} after conditional preprocessing.");

    public override bool HasErrors
        => _diagnostics.Any(d => d.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase));

    public override string BuildChainSummaryLine()
        => $"strip-conditional: {_filesTransformed} transformed, " +
           $"{_filesSkippedClean} clean, " +
           $"{_directivesRemoved} directives removed, " +
           $"{_disabledBranchesRemoved} disabled blocks removed, " +
           $"{_materializedCopies} per-owner copies materialized";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose) => _diagnostics.ToList();
}
