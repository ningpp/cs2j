using CSharpToJava.Core.AsyncNormalize;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// CLI adapter for the C18 async-normalize preprocess pass. Follows the standard
/// spine (no ExecuteAsync/CreateParseOptions override), mirroring
/// <see cref="IteratorLoweringPassAdapter"/>; the residual gate (CS2J1810) runs
/// per file inside the Core preprocessor, so AfterFilesAsync only provides a
/// non-swallowing fallback for the aggregate.
/// </summary>
internal sealed class AsyncNormalizePassAdapter : ProjectPassAdapter
{
    private readonly AsyncNormalizeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly AsyncNormalizePreprocessor _preprocessor = new();
    private readonly List<AsyncNormalizeDiagnostic> _diagnostics = new();
    private readonly AsyncNormalizeStatistics _statistics = new();
    private HashSet<string>? _testProjectDirectories;

    /// <summary>
    /// Restored NuGet compile assemblies of the target solution, enumerated read-only
    /// from <c>obj/project.assets.json</c> in <see cref="OnTreePreparedAsync"/>.
    /// xUnit generic helpers (e.g. <c>Assert.ThrowsAsync&lt;T&gt;</c>) only bind an
    /// async lambda's delegate type when the package assemblies are visible.
    /// </summary>
    private IReadOnlyList<MetadataReference> _packageReferences = Array.Empty<MetadataReference>();

    public AsyncNormalizePassAdapter(
        AsyncNormalizeOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        bool strict)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _strict = strict;
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.AsyncNormalize);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    /// <summary>
    /// Opt-in: the per-file rewrite is a pure function. The shared
    /// <see cref="AsyncNormalizePreprocessor"/> holds no mutable state (every
    /// analyzer/rewriter is a static method or a per-call instance) and the Core
    /// analyzers only read immutable Roslyn state, so the spine may run the read +
    /// rewrite step in parallel. All accumulation (statistics/diagnostics/verbose
    /// lines) is deferred to <see cref="MergeFilePureResult"/>, which the runner
    /// calls serially in source-file order — keeping the summary counts and
    /// diagnostic order identical to the sequential path.
    /// </summary>
    public override bool SupportsParallelFileRewrite => true;

    public override async Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var pure = await RewriteFilePureAsync(context);
        MergeFilePureResult(pure);
        return pure.Rewrite;
    }

    /// <summary>
    /// Pure per-file rewrite for the opt-in parallel path. Reads only the
    /// stateless <see cref="_preprocessor"/> and the immutable
    /// <see cref="_options"/>; no shared field is mutated. Per-file effects are
    /// carried via <see cref="PassFilePureResult.PassState"/> and applied later by
    /// <see cref="MergeFilePureResult"/>.
    /// </summary>
    public override Task<PassFilePureResult> RewriteFilePureAsync(PassFileContext context)
    {
        // Node-level isolation: a test-project file may contain an async void
        // member (CS2J1803) plus independent, safely liftable async lambdas.
        // Enable partial rewrite for that file so the Error gate only keeps the
        // un-normalizable node, not the whole file. The option is immutable per
        // call, keeping the parallel rewrite path race-free.
        var inTestProject = IsInTestProject(context.SourceFilePath);
        var options = inTestProject && !_options.AllowPartialRewriteOnError
            ? _options with { AllowPartialRewriteOnError = true }
            : _options;
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel, options);
        var rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(new PassFilePureResult
        {
            Context = context,
            Rewrite = rewrite,
            PassState = result,
        });
    }

    /// <summary>
    /// Serial merge of one file's pure result, invoked by the runner in
    /// source-file order. Applies statistics/diagnostics and prints the per-file
    /// verbose line here (not in the parallel phase) so the order is preserved.
    /// </summary>
    public override void MergeFilePureResult(PassFilePureResult pure)
    {
        var context = pure.Context;
        var result = (AsyncNormalizeFileResult)pure.PassState!;
        MergeStatistics(result.Statistics);

        // Test projects are .NET-side behaviour oracles, not Java conversion
        // inputs: residual async constructs / async void members there stay
        // valid C# and must not abort the C#→C# chain (same policy as the
        // unsafe-ffm UF050 test-project downgrade). Production projects keep
        // the Error gate.
        var inTestProject = IsInTestProject(context.SourceFilePath);
        foreach (var diagnostic in result.Diagnostics)
        {
            var effective = inTestProject
                && diagnostic.Severity == AsyncNormalizeSeverity.Error
                ? diagnostic with { Severity = AsyncNormalizeSeverity.Warning }
                : diagnostic;
            _diagnostics.Add(effective with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            var status = result.Changed ? "transformed"
                : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                : "dirty-no-rewrite (copy)";
            Console.WriteLine($"[async-normalize] {status}: {context.RelativePath}");
        }
    }

    /// <summary>
    /// Records test-project directories (see the merge-time downgrade) and
    /// enumerates the target solution's restored NuGet compile assemblies so the
    /// semantic model can bind delegate targets declared in package assemblies
    /// (xUnit generic helpers). Enumeration is read-only: no restore is triggered
    /// here (the spine restores after the rewrite phase; restoring per pass would
    /// add a full restore cycle to every pass).
    /// </summary>
    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        if (tree.ProjectGraph is { } graph)
        {
            _testProjectDirectories = graph.ProjectsInTopologicalOrder
                .Where(p => p.Kind == ProjectKind.Test)
                .Select(p => NormalizeDirectory(p.ProjectDirectory))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // The original source tree normally carries the restore artifacts; an
        // intermediate tree only has them when the source itself was already an
        // intermediate (unified-directory chain).
        _packageReferences = EnumeratePackageReferences(tree.Layout.SourceRoot);
        if (_packageReferences.Count == 0)
        {
            _packageReferences = EnumeratePackageReferences(tree.IntermediateRoot);
        }

        if (_verbose && _packageReferences.Count > 0)
        {
            Console.WriteLine(
                $"[async-normalize] binding with {_packageReferences.Count} restored package reference(s)");
        }

        return Task.CompletedTask;
    }

    private static IReadOnlyList<MetadataReference> EnumeratePackageReferences(string root)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<MetadataReference>();
        }

        var references = RestorePackageReferenceEnumerator.Enumerate(root, out _);
        if (references.Count == 0)
        {
            return references;
        }

        // The merged pass compilation unions every project's restored packages, so
        // different projects can contribute different versions of the same
        // assembly (this SDK restores xunit.assert 2.6.6 / 2.9.0 / 2.9.2 / 2.9.3).
        // Referencing two assemblies with the same simple name makes their types
        // ambiguous (CS0433: Xunit.Assert exists in both), so overload resolution
        // never binds the generic Assert helper and the async lambda's delegate
        // target stays null. Keep one assembly per simple name (highest version).
        var bySimpleName = new Dictionary<string, (Version Version, MetadataReference Reference)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            var path = reference.Display ?? string.Empty;
            var simpleName = Path.GetFileNameWithoutExtension(path);
            var version = ParsePackageVersion(path) ?? new Version(0, 0);
            if (!bySimpleName.TryGetValue(simpleName, out var existing)
                || version > existing.Version)
            {
                bySimpleName[simpleName] = (version, reference);
            }
        }

        return bySimpleName.Values.Select(entry => entry.Reference).ToList();
    }

    /// <summary>
    /// Extracts the package version directory (e.g. <c>…/xunit.assert/2.9.3/lib/…</c>)
    /// from a restored assembly path; null when the path carries no version segment.
    /// </summary>
    private static Version? ParsePackageVersion(string path)
    {
        foreach (var segment in path.Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!char.IsDigit(segment[0]))
            {
                continue;
            }

            var numeric = segment.Split('-', '+')[0];
            if (Version.TryParse(numeric, out var version))
            {
                return version;
            }
        }

        return null;
    }

    /// <summary>
    /// TPA runtime assemblies plus the restored NuGet compile assemblies (one per
    /// assembly simple name, see <see cref="EnumeratePackageReferences"/>).
    /// </summary>
    public override IReadOnlyCollection<MetadataReference> GetMetadataReferences()
    {
        var references = new List<MetadataReference>(base.GetMetadataReferences());
        if (_packageReferences.Count > 0)
        {
            references.AddRange(_packageReferences);
        }

        return references;
    }

    private bool IsInTestProject(string sourceFilePath)
    {
        if (_testProjectDirectories is not { Count: > 0 } roots)
        {
            return false;
        }

        var directory = NormalizeDirectory(Path.GetDirectoryName(sourceFilePath));
        var current = new DirectoryInfo(directory);
        while (current != null)
        {
            if (roots.Contains(NormalizeDirectory(current.FullName)))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static string NormalizeDirectory(string? path)
        => path == null
            ? string.Empty
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // The residual gate runs per file inside the Core preprocessor: every
        // residual batch already produced a CS2J1810 diagnostic there (strict
        // mode = Error), and RewriteFileAsync attaches the file path. Do NOT
        // re-emit the same code for the aggregate.
        //
        // Fallback only: the aggregate residual count is positive yet no
        // per-file CS2J1810 was recorded (e.g. the non-strict path keeps
        // counting residuals while suppressing the per-file gate). Surface a
        // single aggregate diagnostic so residuals are never silently
        // swallowed — Error in strict mode, Warning otherwise.
        if (_statistics.ResidualCount > 0
            && !_diagnostics.Any(d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1810))
        {
            var severity = _strict
                ? AsyncNormalizeSeverity.Error
                : AsyncNormalizeSeverity.Warning;
            _diagnostics.Add(new AsyncNormalizeDiagnostic(
                severity,
                AsyncNormalizeDiagnosticCode.CS2J1810,
                $"{_statistics.ResidualCount} residual async construct(s) remain after async normalization " +
                "(un-annotated async lambda/local function, ConfigureAwait call(s), or " +
                "un-exempted async void member(s))."));
        }

        // C38 async-stream counterpart of the C18 fallback: the per-file CS2J1849
        // gate fires only in strict mode; the aggregate stream residual bucket is
        // still populated in non-strict mode (skip-tagged sites are whitelisted
        // per file, so this normally stays zero). Surface one aggregate line so
        // residual 'await foreach'/'await using' sites are never swallowed.
        if (_statistics.AsyncStreamResidualCount > 0
            && !_diagnostics.Any(d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1849))
        {
            var severity = _strict
                ? AsyncNormalizeSeverity.Error
                : AsyncNormalizeSeverity.Warning;
            _diagnostics.Add(new AsyncNormalizeDiagnostic(
                severity,
                AsyncNormalizeDiagnosticCode.CS2J1849,
                $"{_statistics.AsyncStreamResidualCount} residual async-stream site(s) " +
                "('await foreach' or 'await using') remain after async-stream normalization."));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Warning,
            AsyncNormalizeDiagnosticCode.CS2J1820,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Error,
            AsyncNormalizeDiagnosticCode.CS2J1820,
            $"dotnet build failed with exit code {exitCode} after async normalization."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Error,
            AsyncNormalizeDiagnosticCode.CS2J1820,
            $"dotnet test failed with exit code {exitCode} after async normalization."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == AsyncNormalizeSeverity.Error);

    /// <summary>Accumulated pass diagnostics (project-test reconciliation gate).</summary>
    public IReadOnlyList<AsyncNormalizeDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Accumulated C18/C38 statistics (project-test reconciliation gate).</summary>
    public AsyncNormalizeStatistics Statistics => _statistics;

    public override string? BuildChainSummaryLine()
    {
        var lambdasLifted =
            _statistics.AsyncLambdasLiftedZeroCapture
            + _statistics.AsyncLambdasLiftedThisOnly
            + _statistics.AsyncLambdasLiftedVoidWrapper
            + _statistics.AsyncLambdasLiftedCapturedLocals;
        var awaitUsingExpanded =
            _statistics.AwaitUsingStatementsExpanded
            + _statistics.AwaitUsingDeclarationsExpanded;
        // C18 段后追加 C38 分项（0 也输出，保持摘要可 grep 的稳定形态）。
        return $"async-normalize: {lambdasLifted} lambda(s) lifted " +
               $"({_statistics.AsyncLambdasLiftedCapturedLocals} captured), " +
               $"{_statistics.WrappersGenerated} wrapper(s), " +
               $"{_statistics.ConfigureAwaitStripped} ConfigureAwait stripped, " +
               $"{_statistics.AwaitSites} await-site(s) kept, " +
               $"{_statistics.SkippedUnsupported} skipped, " +
               $"{_statistics.AwaitForeachExpanded} await-foreach, " +
               $"{awaitUsingExpanded} await-using, " +
               $"{_statistics.AsyncTypeSitesMapped} async-type sites, " +
               $"{_statistics.AsyncIteratorsRejected} async-iterator rejected, " +
               $"{_statistics.AsyncIteratorsLowered} async-iterator lowered, " +
               $"{_statistics.ResidualCount} residual";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != AsyncNormalizeSeverity.Info || verbose)
            .Select(d =>
            {
                var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
                return $"[{d.Severity}] {prefix}{d.Message}";
            })
            .ToList();

    private void MergeStatistics(AsyncNormalizeStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.ResidualCount += source.ResidualCount;

        _statistics.AsyncMethodsSeen += source.AsyncMethodsSeen;
        _statistics.AwaitSites += source.AwaitSites;
        _statistics.ConfigureAwaitStripped += source.ConfigureAwaitStripped;
        _statistics.AsyncVoidRejected += source.AsyncVoidRejected;
        _statistics.UnsupportedAwaiters += source.UnsupportedAwaiters;
        _statistics.SkippedUnsupported += source.SkippedUnsupported;

        _statistics.AsyncLambdasLiftedZeroCapture += source.AsyncLambdasLiftedZeroCapture;
        _statistics.AsyncLambdasLiftedThisOnly += source.AsyncLambdasLiftedThisOnly;
        _statistics.AsyncLambdasLiftedCapturedLocals += source.AsyncLambdasLiftedCapturedLocals;
        _statistics.AsyncLambdasLiftedVoidWrapper += source.AsyncLambdasLiftedVoidWrapper;
        _statistics.AsyncLocalFunctionsLifted += source.AsyncLocalFunctionsLifted;
        _statistics.AsyncLambdasLiftedInPartialRewrite += source.AsyncLambdasLiftedInPartialRewrite;
        _statistics.WrappersGenerated += source.WrappersGenerated;
        _statistics.CapturedAsyncLambdasDeferred += source.CapturedAsyncLambdasDeferred;

        // C38 M2/3a + M3/3b buckets.
        _statistics.AwaitUsingStatementsExpanded += source.AwaitUsingStatementsExpanded;
        _statistics.AwaitUsingDeclarationsExpanded += source.AwaitUsingDeclarationsExpanded;
        _statistics.AsyncDisposeGuardsEmitted += source.AsyncDisposeGuardsEmitted;
        _statistics.AsyncTypeSitesMapped += source.AsyncTypeSitesMapped;
        _statistics.AsyncTypeSitesRejected += source.AsyncTypeSitesRejected;
        _statistics.DuckTypedAsyncDeferred += source.DuckTypedAsyncDeferred;
        _statistics.AwaitForeachExpanded += source.AwaitForeachExpanded;
        _statistics.AsyncEnumeratorsEmitted += source.AsyncEnumeratorsEmitted;
        _statistics.WithCancellationResolved += source.WithCancellationResolved;
        _statistics.EnumerableConfigureAwaitStripped += source.EnumerableConfigureAwaitStripped;
        _statistics.AsyncIteratorsRejected += source.AsyncIteratorsRejected;
        _statistics.AsyncIteratorsLowered += source.AsyncIteratorsLowered;
        _statistics.AsyncIteratorClassesEmitted += source.AsyncIteratorClassesEmitted;
        _statistics.AsyncIteratorRegionsEmitted += source.AsyncIteratorRegionsEmitted;
        _statistics.AsyncStreamResidualCount += source.AsyncStreamResidualCount;
    }
}
