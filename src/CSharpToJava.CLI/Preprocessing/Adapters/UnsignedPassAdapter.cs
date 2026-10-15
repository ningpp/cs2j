using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the unsigned-type rewrite pass. Preserves the exact statistics merge,
/// own-release-library exclusion, Cs2jUnsignedMath injection/project-reference wiring,
/// BOM handling and diagnostic wording of the former ProjectUnsignedTypePreprocessor.
/// Support-library release/injection lives in <see cref="Cs2jUnsignedMathInjection"/>
/// and is shared with numeric-boundary.
/// </summary>
internal sealed class UnsignedPassAdapter : ProjectPassAdapter
{
    private readonly UnsignedTypeRewriterOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly UnsignedTypeRewriter _rewriter = new();
    private readonly List<UnsignedTypeRewriterDiagnostic> _diagnostics = new();
    private readonly UnsignedTypeRewriterStatistics _statistics = new();
    private readonly List<string> _rewrittenSourceFiles = new();
    private List<string> _ownReleaseDirectories = new();

    public UnsignedPassAdapter(
        UnsignedTypeRewriterOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.Unsigned);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree) => new();

    public UnsignedTypeRewriterStatistics Statistics => _statistics;
    public IReadOnlyList<UnsignedTypeRewriterDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Opt-in: the per-file rewrite is a pure function — the shared
    /// <see cref="UnsignedTypeRewriter"/> holds no mutable state (each call builds
    /// its own syntax rewriters and works on immutable static tables), so the
    /// spine may run the read + rewrite step in parallel. Every accumulation
    /// (statistics, diagnostics, the rewritten-file list, verbose lines) is
    /// deferred to <see cref="MergeFilePureResult"/>, which the runner calls
    /// serially in source-file order — keeping summary counts and diagnostic
    /// order identical to the sequential path.
    /// </summary>
    public override bool SupportsParallelFileRewrite => true;

    /// <summary>Sentinel <see cref="PassFilePureResult.PassState"/> for files under a
    /// previously released support-library tree (no rewrite, no statistics).</summary>
    private static readonly object OwnReleaseLibMarker = new();

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _ownReleaseDirectories = Cs2jUnsignedMathInjection.FindOwnReleaseLibDirectories(tree.SourceRoot);
        return Task.CompletedTask;
    }

    public override async Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Single source of truth for both spines: rewrite purely, then merge.
        var pure = await RewriteFilePureAsync(context);
        MergeFilePureResult(pure);
        return pure.Rewrite;
    }

    /// <summary>
    /// Pure per-file rewrite for the opt-in parallel path: reads only immutable
    /// adapter state and calls the stateless rewriter; no shared field is
    /// mutated, so it is safe to call concurrently. Per-file effects travel
    /// through <see cref="PassFilePureResult.PassState"/> and are applied later by
    /// <see cref="MergeFilePureResult"/>.
    /// </summary>
    public override Task<PassFilePureResult> RewriteFilePureAsync(PassFileContext context)
    {
        if (Cs2jUnsignedMathInjection.IsUnderOwnReleaseLib(context.SourceFilePath, _ownReleaseDirectories))
        {
            // Support-library code is never rewritten; its verbose line is
            // deferred to the serial merge so console order stays identical.
            return Task.FromResult(new PassFilePureResult
            {
                Context = context,
                Rewrite = null,
                PassState = OwnReleaseLibMarker,
            });
        }

        var result = _rewriter.Rewrite(context.SourceCode, context.SemanticModel!, _options);
        PassFileRewrite? rewrite = null;
        if (result.Changed)
        {
            rewrite = new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            };
        }

        return Task.FromResult(new PassFilePureResult
        {
            Context = context,
            Rewrite = rewrite,
            PassState = result,
        });
    }

    /// <summary>
    /// Serial merge of one file's pure result, invoked by the runner in
    /// source-file order. Applies statistics/diagnostics/the rewritten-file list
    /// exactly as the sequential loop did, and prints the verbose line here (not
    /// in the parallel phase) so its order is preserved.
    /// </summary>
    public override void MergeFilePureResult(PassFilePureResult pure)
    {
        var context = pure.Context;
        if (ReferenceEquals(pure.PassState, OwnReleaseLibMarker))
        {
            if (_verbose)
            {
                Console.WriteLine($"[unsigned-type] clean (own release copy): {context.RelativePath}");
            }

            return;
        }

        var result = (UnsignedTypeRewriterResult)pure.PassState!;
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { Message = $"{context.RelativePath}: {diagnostic.Message}" });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[unsigned-type] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            _rewrittenSourceFiles.Add(context.SourceFilePath);
        }
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_statistics.OperatorsRewritten > 0 || _statistics.CastsRewritten > 0)
        {
            await Cs2jUnsignedMathInjection.ReleaseAndInjectAsync(
                tree,
                _rewrittenSourceFiles,
                message => _diagnostics.Add(
                    new UnsignedTypeRewriterDiagnostic(UnsignedTypeSeverity.Warning, message)));
        }
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new UnsignedTypeRewriterDiagnostic(
            UnsignedTypeSeverity.Error,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved, leading to incorrect code generation."));

    public override void OnBuildFailed(int exitCode)
    {
        // Unsigned historically does not run verify builds.
    }

    public override void OnTestsFailed(int exitCode)
    {
        // Unsigned historically does not run verify tests.
    }

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == UnsignedTypeSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"unsigned-type: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.TypesReplaced} types replaced, " +
           $"{_statistics.OperatorsRewritten} operators rewritten, " +
           $"{_statistics.LiteralsRewritten} literals rewritten";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d => $"[{d.Severity}] {d.Message}")
            .ToList();

    private void MergeStatistics(UnsignedTypeRewriterStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.TypesReplaced += source.TypesReplaced;
        _statistics.OperatorsRewritten += source.OperatorsRewritten;
        _statistics.CastsRewritten += source.CastsRewritten;
        _statistics.ArraysRewritten += source.ArraysRewritten;
        _statistics.GenericArgsReplaced += source.GenericArgsReplaced;
        _statistics.LiteralsRewritten += source.LiteralsRewritten;
    }
}
