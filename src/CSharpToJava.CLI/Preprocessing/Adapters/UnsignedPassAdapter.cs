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

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _ownReleaseDirectories = Cs2jUnsignedMathInjection.FindOwnReleaseLibDirectories(tree.SourceRoot);
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        if (Cs2jUnsignedMathInjection.IsUnderOwnReleaseLib(context.SourceFilePath, _ownReleaseDirectories))
        {
            if (_verbose)
            {
                Console.WriteLine($"[unsigned-type] clean (own release copy): {context.RelativePath}");
            }

            return Task.FromResult<PassFileRewrite?>(null);
        }

        var result = _rewriter.Rewrite(context.SourceCode, context.SemanticModel!, _options);
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
            var rewrite = new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            };
            return Task.FromResult<PassFileRewrite?>(rewrite);
        }

        return Task.FromResult<PassFileRewrite?>(null);
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
