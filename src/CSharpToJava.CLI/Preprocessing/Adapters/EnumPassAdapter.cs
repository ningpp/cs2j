using CSharpToJava.Core.EnumPreprocessor;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the enum/bitwise/HasFlag preprocess pass. Preserves the exact
/// statistics merge, residual gate and diagnostic wording of the former
/// ProjectEnumPreprocessor facade.
/// </summary>
internal sealed class EnumPassAdapter : ProjectPassAdapter
{
    private readonly EnumPreprocessorOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly EnumPreprocessor _rewriter = new();
    private readonly List<EnumPreprocessorDiagnostic> _diagnostics = new();
    private readonly EnumPreprocessorStatistics _statistics = new();

    public EnumPassAdapter(
        EnumPreprocessorOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.Enum);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public EnumPreprocessorStatistics Statistics => _statistics;
    public IReadOnlyList<EnumPreprocessorDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Opt-in: the per-file rewrite is a pure function (the shared
    /// <see cref="EnumPreprocessor"/> holds no mutable state — each call builds its
    /// own statistics/rewriter and an AdhocWorkspace), so the spine may run the read
    /// + rewrite step in parallel. Every accumulation is deferred to
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
        var result = _rewriter.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        PassFileRewrite? rewrite = result.Changed
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
        var result = (EnumPreprocessorResult)pure.PassState!;
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            var status = result.Changed ? "transformed"
                : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                : "dirty-no-rewrite (copy)";
            Console.WriteLine($"[enum] {status}: {context.RelativePath}");
        }
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_statistics.ResidualCount > 0)
        {
            var severity = _strict
                ? EnumPreprocessorSeverity.Error
                : EnumPreprocessorSeverity.Warning;
            _diagnostics.Add(new EnumPreprocessorDiagnostic(
                severity,
                $"{_statistics.ResidualCount} residual enum bitwise/HasFlag node(s) remaining after preprocessing."));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new EnumPreprocessorDiagnostic(
            EnumPreprocessorSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new EnumPreprocessorDiagnostic(
            EnumPreprocessorSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after enum preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new EnumPreprocessorDiagnostic(
            EnumPreprocessorSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after enum preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == EnumPreprocessorSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"enum: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.FilesDirtyNoRewrite} dirty-no-rewrite, " +
           $"{_statistics.BitwiseOrRewritten + _statistics.BitwiseAndRewritten + _statistics.BitwiseXorRewritten} bitwise, " +
           $"{_statistics.BitwiseNotRewritten} ~, " +
           $"{_statistics.CompoundAssignRewritten} compound, " +
           $"{_statistics.HasFlagRewritten} HasFlag, " +
           $"{_statistics.NullableLiftedRewritten} lifted, " +
           $"{_statistics.ResidualCount} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d =>
            {
                var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
                return $"[{d.Severity}] {prefix}{d.Message}";
            })
            .ToList();

    private void MergeStatistics(EnumPreprocessorStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.BitwiseOrRewritten += source.BitwiseOrRewritten;
        _statistics.BitwiseAndRewritten += source.BitwiseAndRewritten;
        _statistics.BitwiseXorRewritten += source.BitwiseXorRewritten;
        _statistics.BitwiseNotRewritten += source.BitwiseNotRewritten;
        _statistics.CompoundAssignRewritten += source.CompoundAssignRewritten;
        _statistics.HasFlagRewritten += source.HasFlagRewritten;
        _statistics.NullableLiftedRewritten += source.NullableLiftedRewritten;
        _statistics.SkippedUnsupported += source.SkippedUnsupported;
        _statistics.ResidualCount += source.ResidualCount;
    }
}
