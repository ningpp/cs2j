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

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(
            context.SourceCode, context.SemanticModel!, _options);
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

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true, PreserveBom = true }
            : (PassFileRewrite?)null);
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
