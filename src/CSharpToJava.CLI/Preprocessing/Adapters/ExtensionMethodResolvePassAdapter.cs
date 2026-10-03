using System.Text;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the extension-methods pass (C19): whole-tree bind+analyze once,
/// per-file rewrite, post-rewrite residual scan. Structurally close to
/// <see cref="GenericDeepenPassAdapter"/> but with NO generated-file
/// distribution (no holders, no new files). M0: rules are empty, so every file
/// is byte-copied (Changed=false; BOM preserved) and the intermediate tree is
/// identical to the input.
/// </summary>
internal sealed class ExtensionMethodResolvePassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-extension-methods-src";

    /// <summary>
    /// Reserved intermediate-tree directory names of the five-anchor injected
    /// support libraries (design §4.2-2). A source symbol declared under one of
    /// these segments is classified InCompilationSupport rather than user code.
    /// Cs2jTuples intentionally has no entry: its products are struct files
    /// distributed into user projects and stay user-owned.
    /// </summary>
    private static readonly HashSet<string> SupportLibraryDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ArrayHelperSupportFiles.LibDirectoryName,
        PreludeSupportFiles.LibDirectoryName,
        RefOutInSupportFiles.LibDirectoryName,
        UnsignedMathSupportFiles.LibDirectoryName,
    };

    private readonly ExtensionMethodResolveOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly Func<INamedTypeSymbol, ExternalHostMappingKind>? _externalHostMapper;
    private readonly ExtensionHostOverrides? _hostOverrides;

    /// <summary>M4 drill seam: force selected sites to emit non-compiling code (tests only).</summary>
    private readonly Func<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax, IMethodSymbol, bool>? _forceBrokenRewrite;

    private readonly ExtensionMethodResolvePreprocessor _preprocessor = new();
    private readonly List<ExtensionDiagnostic> _diagnostics = new();
    private readonly ExtensionMethodResolveStatistics _statistics = new();

    private ExtensionMethodResolveOptions _effectiveOptions = new();
    private IReadOnlyList<MetadataReference> _enhancedReferences = Array.Empty<MetadataReference>();
    private HashSet<ExtensionRewriteErrorKey> _baselineErrorKeys = new();
    private PassTreeContext? _tree;
    private bool _prepared;

    public ExtensionMethodResolvePassAdapter(
        ExtensionMethodResolveOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        Func<INamedTypeSymbol, ExternalHostMappingKind>? externalHostMapper = null,
        ExtensionHostOverrides? hostOverrides = null,
        Func<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax, IMethodSymbol, bool>? forceBrokenRewrite = null)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _externalHostMapper = externalHostMapper;
        _hostOverrides = hostOverrides ?? options.HostOverrides;
        _forceBrokenRewrite = forceBrokenRewrite;
        _preprocessor.ForceBrokenRewrite = forceBrokenRewrite;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.ExtensionMethods);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    /// <summary>C19 explicitly parses at the latest language version (design §4.1; C#14 block probe relies on it).</summary>
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);

    /// <summary>
    /// ImplicitUsings=enable projects rely on SDK-generated GlobalUsings.g.cs;
    /// the one-shot binding compilation must mirror them, otherwise extension
    /// lookups whose receivers live in System.Collections.Generic / System.Linq
    /// fail overload resolution and the sites are falsely reported unbound.
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

    public ExtensionMethodResolveStatistics Statistics => _statistics;
    public IReadOnlyList<ExtensionDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// M3 (design §4.2-4/5, appendix ruling 2): the binding compilation needs
    /// the restored NuGet assemblies, but the standard spine only restores
    /// after per-file rewrites. Run the tree's restore here (the later spine
    /// restore is an incremental no-op) and enumerate the per-project compile
    /// assemblies for <see cref="GetMetadataReferences"/>. Also freezes the
    /// effective options, including the single-file/loose-directory flag.
    /// </summary>
    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        if (_prepared)
        {
            return Task.CompletedTask;
        }

        _prepared = true;
        _tree = tree;

        var singleFile = tree.Layout.ProjectFiles.Count == 0;
        _effectiveOptions = _options with
        {
            ExternalHostMapper = _externalHostMapper,
            HostOverrides = _hostOverrides,
            IsSupportLibraryHost = _options.IsSupportLibraryHost ?? IsSupportLibraryHostSymbol,
            SingleFileIncompleteReferences = singleFile,
            ReportSiteDetail = _options.ReportSiteDetail || _verbose,
        };

        // No project graph → nothing to restore; unbound extension-like sites
        // take the conservative CS2J1290 path.
        if (!singleFile)
        {
            var restoreExitCode = ProjectPassRunner.Restore(this, tree);
            if (restoreExitCode == 0)
            {
                _enhancedReferences = RestorePackageReferenceEnumerator.Enumerate(
                    tree.IntermediateRoot, out var addedCount);
                if (_verbose && addedCount > 0)
                {
                    Console.WriteLine($"[extension-methods] binding with {addedCount} restored package reference(s)");
                }
            }
        }
        else if (_verbose)
        {
            Console.WriteLine("[extension-methods] single-file/loose-directory layout: CS2J1290 conservative mode armed");
        }

        return Task.CompletedTask;
    }

    /// <summary>TPA basics plus the restored NuGet compile assemblies (M3 ruling 2).</summary>
    public override IReadOnlyCollection<MetadataReference> GetMetadataReferences()
    {
        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        if (_enhancedReferences.Count > 0)
        {
            references.AddRange(_enhancedReferences);
        }

        return references;
    }

    private static bool IsSupportLibraryHostSymbol(INamedTypeSymbol host)
    {
        foreach (var reference in host.DeclaringSyntaxReferences)
        {
            if (IsUnderSupportLibraryDirectory(reference.SyntaxTree.FilePath))
            {
                return true;
            }
        }

        foreach (var location in host.Locations)
        {
            if (location.IsInSource
                && location.SourceTree is { } tree
                && IsUnderSupportLibraryDirectory(tree.FilePath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderSupportLibraryDirectory(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        foreach (var segment in filePath.Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (SupportLibraryDirectoryNames.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        _preprocessor.BindCompilation(compilation);
        _preprocessor.Analyze(_effectiveOptions);

        // M4 rollback gate baseline (design §4.4-4): error keys of the ORIGINAL
        // tree compiled with the same references/options the candidate uses.
        // Location paths are remapped into the intermediate layout so the
        // triples diff directly against candidate diagnostics.
        _baselineErrorKeys = new HashSet<ExtensionRewriteErrorKey>(
            BuildErrorKeys(compilation, remapToIntermediate: true));
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

        if (_verbose)
        {
            Console.WriteLine($"[extension-methods] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        if (!_effectiveOptions.RunResidualScan)
        {
            return Task.CompletedTask;
        }

        var candidate = BuildIntermediateCompilation(tree);

        // M4 §4.4-4: candidate-vs-baseline compile-error diff with file
        // attribution; unique rewritten file → replay that file; otherwise
        // whole-tree rollback + CS2J1200.
        var candidateKeys = BuildErrorKeys(candidate, remapToIntermediate: false);
        var rewrittenDest = MapRewrittenToDestination(tree);
        var rollbackPlan = ExtensionRewriteRollback.Plan(
            _baselineErrorKeys, candidateKeys, rewrittenDest);

        HashSet<string>? excludedFiles = null;
        if (rollbackPlan.Verdict == ExtensionRollbackVerdict.ExcludeFiles)
        {
            excludedFiles = RevertFiles(tree, rollbackPlan.FilesToRevert, rollbackPlan.NewErrors);
            candidate = BuildIntermediateCompilation(tree);
        }
        else if (rollbackPlan.Verdict == ExtensionRollbackVerdict.WholeTreeRollback)
        {
            RevertFiles(tree, rewrittenDest, rollbackPlan.NewErrors);
            EmitWholeTreeRollback(rollbackPlan.NewErrors, tree.IntermediateRoot);
            return Task.CompletedTask;
        }

        RunResidualScan(tree, candidate, excludedFiles);
        return Task.CompletedTask;
    }

    /// <summary>Byte-exact replay of the given intermediate files from their original copies.</summary>
    private HashSet<string> RevertFiles(
        PassTreeContext tree,
        IReadOnlyCollection<string> intermediateFiles,
        IReadOnlyList<ExtensionRewriteErrorKey> newErrors)
    {
        var reverted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errorSummary = string.Join("; ", newErrors
            .Select(e => $"{Path.GetFileName(e.File)}({e.Id})")
            .Distinct(StringComparer.Ordinal));

        foreach (var dest in intermediateFiles)
        {
            var relative = Path.GetRelativePath(tree.IntermediateRoot, dest);
            var source = Path.Combine(tree.SourceRoot, relative);
            File.Copy(source, dest, overwrite: true);
            reverted.Add(dest);

            Console.WriteLine(
                $"[extension-methods] rollback: reverted {relative} (new compile error(s) vs baseline: {errorSummary}); "
                + "file replayed byte-for-byte from the original.");

            _diagnostics.Add(new ExtensionDiagnostic(
                ExtensionSeverity.Info,
                ExtensionRule.Infra,
                ExtensionMethodCodes.Info,
                $"EM001 build-failure rollback: file '{relative}' reverted to the original copy because the rewritten "
                + $"candidate added compile error(s) vs the baseline tree [{errorSummary}].",
                relative));
        }

        _statistics.FilesRolledBack += reverted.Count;
        return reverted;
    }

    private void EmitWholeTreeRollback(IReadOnlyList<ExtensionRewriteErrorKey> newErrors, string root)
    {
        var detail = string.Join("; ", newErrors
            .Take(5)
            .Select(e => $"{Path.GetFileName(e.File)}({e.Id}): {e.Message}"));
        Console.Error.WriteLine(
            "[extension-methods] whole-tree rollback (CS2J1200): new compile error(s) could not be attributed to a "
            + $"single rewritten file; every rewritten file was replayed from the originals. First errors: {detail}");

        _diagnostics.Add(new ExtensionDiagnostic(
            ExtensionSeverity.Error,
            ExtensionRule.Infra,
            ExtensionMethodCodes.WholeTreeRollback,
            "The rewritten candidate introduced compile error(s) that could not be attributed uniquely to one rewritten "
            + $"file [{detail}]; all changes from this run were rolled back to the original inputs (CS2J1200).",
            root));
    }

    /// <summary>Post-rewrite gate (design §4.5): recompile the intermediate tree and count leftover shapes.</summary>
    private void RunResidualScan(PassTreeContext tree, CSharpCompilation compilation, HashSet<string>? excludedFiles)
    {
        _statistics.ThisModifierBaseline = _preprocessor.ThisModifierBaseline;
        _statistics.ThisModifierPreserved = compilation.SyntaxTrees
            .Where(t => excludedFiles is null || !excludedFiles.Contains(t.FilePath))
            .Sum(ExtensionMethodResidualScanner.CountExtensionThisModifiers);

        foreach (var (rule, count) in _preprocessor.ResidualScan(compilation, excludedFiles))
        {
            _statistics.ResidualsByRule.TryGetValue(rule, out var existing);
            _statistics.ResidualsByRule[rule] = existing + count;
            _diagnostics.Add(new ExtensionDiagnostic(
                ExtensionMethodResolvePolicy.ResidualSeverity(_effectiveOptions.Strict),
                ExtensionRule.Residual,
                ExtensionMethodCodes.ResidualFound,
                $"{count} residual site(s) of rule '{rule}' remain after extension-methods; the rewrite did not cover them (design §4.5).",
                tree.IntermediateRoot));
        }
    }

    private CSharpCompilation BuildIntermediateCompilation(PassTreeContext tree)
    {
        var parseOptions = CreateParseOptions(tree);
        var trees = new List<SyntaxTree>();
        foreach (var file in Directory.EnumerateFiles(tree.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, path: file));
        }

        // Mirror the SDK implicit global usings for gate binding as well.
        trees.AddRange(CreateAdditionalSyntaxTrees(tree));

        return CSharpCompilation.Create(
            CompilationName ?? "cs2j-extension-methods-residual",
            trees, GetMetadataReferences(), CreateCompilationOptions());
    }

    private List<ExtensionRewriteErrorKey> BuildErrorKeys(
        CSharpCompilation compilation,
        bool remapToIntermediate)
    {
        var tree = _tree;
        var keys = new List<ExtensionRewriteErrorKey>();
        foreach (var diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }

            var file = diagnostic.Location.SourceTree?.FilePath;
            if (file is not null && remapToIntermediate && tree is not null)
            {
                file = RemapSourceToIntermediate(file, tree);
            }

            keys.Add(new ExtensionRewriteErrorKey(
                file ?? string.Empty,
                diagnostic.Descriptor.Id,
                diagnostic.GetMessage()));
        }

        return keys;
    }

    private static string RemapSourceToIntermediate(string sourcePath, PassTreeContext tree)
    {
        var normalizedSource = sourcePath.Replace('\\', '/');
        var normalizedRoot = tree.SourceRoot.Replace('\\', '/');
        if (!normalizedSource.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalizedSource, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        var relative = Path.GetRelativePath(tree.SourceRoot, sourcePath);
        return Path.Combine(tree.IntermediateRoot, relative);
    }

    private HashSet<string> MapRewrittenToDestination(PassTreeContext tree)
        => new(
            _preprocessor.RewrittenFiles.Select(f => RemapSourceToIntermediate(f, tree)),
            StringComparer.OrdinalIgnoreCase);

    // Design §6: CS2J1230 is Error for both restore and build failures; CS2J1231 for test failures.

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ExtensionDiagnostic(
            ExtensionSeverity.Error,
            ExtensionRule.Infra,
            ExtensionMethodCodes.RestoreOrBuildFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ExtensionDiagnostic(
            ExtensionSeverity.Error,
            ExtensionRule.Infra,
            ExtensionMethodCodes.RestoreOrBuildFailed,
            $"dotnet build failed with exit code {exitCode} after extension-methods preprocessing; the rewrite produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ExtensionDiagnostic(
            ExtensionSeverity.Error,
            ExtensionRule.Infra,
            ExtensionMethodCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after extension-methods preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ExtensionSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != ExtensionSeverity.Info || verbose)
            .Select(d => d.ToString())
            .ToList();
}
