using CSharpToJava.Core.PatternDesugar;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C03 pattern statement desugar pass. Mirrors the enum
/// adapter's statistics merge, residual gate and diagnostic handling.
/// </summary>
internal sealed class PatternDesugarPassAdapter : ProjectPassAdapter
{
    private readonly PatternDesugarOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly PatternDesugarPreprocessor _preprocessor = new();
    private readonly List<PatternDesugarDiagnostic> _diagnostics = new();
    private readonly PatternDesugarStatistics _statistics = new();

    public PatternDesugarPassAdapter(
        PatternDesugarOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.PatternDesugar);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public PatternDesugarStatistics Statistics => _statistics;
    public IReadOnlyList<PatternDesugarDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            if (diagnostic.Code == PatternDesugarCodes.UnsupportedPattern
                && _options.ResidualWhitelist != null
                && _options.ResidualWhitelist.Any(
                    w => context.RelativePath.Contains(w, StringComparison.Ordinal)))
            {
                continue; // whitelisted file: PD010 acknowledged, not an error
            }
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            var status = result.Changed ? "transformed"
                : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                : "dirty-no-rewrite (copy)";
            Console.WriteLine($"[pattern-desugar] {status}: {context.RelativePath}");
        }

        var rewrite = result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : null;
        return Task.FromResult(rewrite);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_statistics.ResidualCount > 0)
        {
            _diagnostics.Add(new PatternDesugarDiagnostic(
                PatternDesugarCodes.ResidualTargets,
                _strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                $"{_statistics.ResidualCount} residual pattern/catch target node(s) remaining after pattern desugaring."));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new PatternDesugarDiagnostic(
            PatternDesugarCodes.RestoreOrBuildFailed,
            PatternDesugarSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new PatternDesugarDiagnostic(
            PatternDesugarCodes.RestoreOrBuildFailed,
            PatternDesugarSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after pattern desugaring."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new PatternDesugarDiagnostic(
            PatternDesugarCodes.RestoreOrBuildFailed,
            PatternDesugarSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after pattern desugaring."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == PatternDesugarSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"pattern-desugar: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.FilesDirtyNoRewrite} dirty-no-rewrite, " +
           $"{_statistics.PatternSwitchesRewritten} pattern-switch, " +
           $"{_statistics.NonIntegerSwitchesRewritten} non-int-switch, " +
           $"{_statistics.CatchFiltersRewritten} catch-filter, " +
           $"{_statistics.CatchTypeParamRewritten} catch-T, " +
           $"{_statistics.GoverningTempsIntroduced} govs, " +
           $"{_statistics.EndLabelsIntroduced} end-labels, " +
           $"{_statistics.UnsupportedSkipped} skipped, " +
           $"{_statistics.ResidualCount} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d =>
            {
                var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
                return $"[{d.Severity}] {prefix}{d.Message}";
            })
            .ToList();

    private void MergeStatistics(PatternDesugarStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.PatternSwitchesRewritten += source.PatternSwitchesRewritten;
        _statistics.NonIntegerSwitchesRewritten += source.NonIntegerSwitchesRewritten;
        _statistics.CatchFiltersRewritten += source.CatchFiltersRewritten;
        _statistics.CatchTypeParamRewritten += source.CatchTypeParamRewritten;
        _statistics.GoverningTempsIntroduced += source.GoverningTempsIntroduced;
        _statistics.EndLabelsIntroduced += source.EndLabelsIntroduced;
        _statistics.UnsupportedSkipped += source.UnsupportedSkipped;
        _statistics.ResidualCount += source.ResidualCount;
    }
}
