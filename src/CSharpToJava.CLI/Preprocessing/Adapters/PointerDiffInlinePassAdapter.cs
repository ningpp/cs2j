using CSharpToJava.Core.PointerDiffInline;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the independent <c>pointer-diff-inline</c> expression pass. It uses
/// the standard <see cref="ProjectPassAdapter"/> spine (semantic model required)
/// and rewrites each file through <see cref="PointerDiffInlinePreprocessor"/>:
/// file-local <c>PtrDiff(left, right)</c> helpers whose body is a provable
/// same-type pointer difference are inlined to <c>(R)(left - right)</c> and their
/// declarations are removed when unreferenced. Helpers that are not a provable
/// same-type pointer difference stay verbatim with PDI001. There is no support
/// library and no generated file.
/// </summary>
internal sealed class PointerDiffInlinePassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly PointerDiffInlinePreprocessor _preprocessor;
    private readonly List<PointerDiffInlineDiagnostic> _diagnostics = new();
    private readonly PointerDiffInlineStatistics _statistics = new();

    public PointerDiffInlinePassAdapter(
        PointerDiffInlineOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _preprocessor = new PointerDiffInlinePreprocessor(options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.PointerDiffInline);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "pointer-diff-inline-preprocess";

    public PointerDiffInlineStatistics Statistics => _statistics;
    public IReadOnlyList<PointerDiffInlineDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.RewriteFile(
            context.SourceCode, context.SemanticModel!);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[pointer-diff-inline] {(result.Changed ? "transformed" : "clean (copy)")}: " +
                context.RelativePath);
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
        // No support library and no generated file: the standard spine already
        // wrote every clean/rewritten file.
        => Task.CompletedTask;

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new PointerDiffInlineDiagnostic(
            PointerDiffInlineSeverity.Warning,
            PointerDiffInlineDiagnosticCode.PDI010,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new PointerDiffInlineDiagnostic(
            PointerDiffInlineSeverity.Error,
            PointerDiffInlineDiagnosticCode.PDI010,
            $"dotnet build failed with exit code {exitCode} after pointer-diff-inline " +
            "preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new PointerDiffInlineDiagnostic(
            PointerDiffInlineSeverity.Error,
            PointerDiffInlineDiagnosticCode.PDI010,
            $"dotnet test failed with exit code {exitCode} after pointer-diff-inline " +
            "preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Severity == PointerDiffInlineSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"pointer-diff-inline: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.PointerDiffMethodsInlined} helper(s) inlined, " +
           $"{_statistics.PointerDiffCallSitesInlined} call site(s) rewritten, " +
           $"{_statistics.PointerDiffMethodsRemovedUnused} unused helper(s) removed, " +
           $"{_statistics.PointerDiffMethodsPreserved} preserved (PDI001)";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != PointerDiffInlineSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private void MergeStatistics(PointerDiffInlineStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.PointerDiffMethodsInlined += source.PointerDiffMethodsInlined;
        _statistics.PointerDiffMethodsRemovedUnused += source.PointerDiffMethodsRemovedUnused;
        _statistics.PointerDiffCallSitesInlined += source.PointerDiffCallSitesInlined;
        _statistics.PointerDiffMethodsPreserved += source.PointerDiffMethodsPreserved;
    }
}
