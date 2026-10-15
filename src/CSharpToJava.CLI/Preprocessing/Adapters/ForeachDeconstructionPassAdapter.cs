using CSharpToJava.Core.ForeachDeconstruction;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the foreach-deconstruction lowering pass
/// (former ProjectForeachDeconstructionPreprocessor).
/// </summary>
internal sealed class ForeachDeconstructionPassAdapter : ProjectPassAdapter
{
    private readonly ForeachDeconstructionOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly ForeachDeconstructionPreprocessor _preprocessor = new();
    private readonly List<ForeachDeconstructionDiagnostic> _diagnostics = new();
    private readonly ForeachDeconstructionStatistics _statistics = new();

    public ForeachDeconstructionPassAdapter(
        ForeachDeconstructionOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.ForeachDeconstruction);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;

    // Historical behavior: this pass parses with default options (no NETCOREAPP symbol).
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new();

    public ForeachDeconstructionStatistics Statistics => _statistics;
    public IReadOnlyList<ForeachDeconstructionDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Opt-in: the per-file rewrite is a pure function (the shared
    /// <see cref="ForeachDeconstructionPreprocessor"/> holds no mutable state — each
    /// call builds its own statistics/rewriter and an AdhocWorkspace), so the spine
    /// may run the read + rewrite step in parallel. Every accumulation is deferred to
    /// <see cref="MergeFilePureResult"/>, which the runner calls serially in
    /// source-file order — keeping the summary counts and diagnostic order identical
    /// to the sequential path.
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
        var result = _preprocessor.Preprocess(
            context.SourceCode, context.SemanticModel!, _options);
        PassFileRewrite? rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true, PreserveBom = true }
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
        var result = (ForeachDeconstructionResult)pure.PassState!;
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { Message = $"{context.RelativePath}: {diagnostic.Message}" });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[foreach-deconstruction] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ForeachDeconstructionDiagnostic(
            ForeachDeconstructionSeverity.Error,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ForeachDeconstructionDiagnostic(
            ForeachDeconstructionSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after foreach deconstruction."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ForeachDeconstructionDiagnostic(
            ForeachDeconstructionSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after foreach deconstruction."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ForeachDeconstructionSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"foreach-deconstruction: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.ForeachDeconstructionsLowered} lowered, " +
           $"{_statistics.ForeachDeconstructionsSkipped} skipped";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != ForeachDeconstructionSeverity.Info || verbose)
            .Select(d => $"[{d.Severity}] {d.Message}")
            .ToList();

    private void MergeStatistics(ForeachDeconstructionStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.ForeachDeconstructionsLowered += source.ForeachDeconstructionsLowered;
        _statistics.ForeachDeconstructionsSkipped += source.ForeachDeconstructionsSkipped;
    }
}
