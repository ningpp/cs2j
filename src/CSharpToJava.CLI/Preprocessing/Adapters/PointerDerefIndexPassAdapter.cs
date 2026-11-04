using CSharpToJava.Core.PointerDerefIndex;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the independent <c>pointer-deref-index</c> post-pass. It uses the
/// standard <see cref="ProjectPassAdapter"/> spine (semantic model required) and
/// rewrites each file through <see cref="PointerDerefIndexPreprocessor"/>:
/// <c>*p</c> / <c>*(p + k)</c> / <c>*(T*)expr</c> become <c>p[0]</c> /
/// <c>p[k]</c> / <c>((T*)expr)[0]</c>, while side-effect receivers and
/// unprovable shapes are preserved with a PDI001 warning. There is no support
/// library and no generated file.
/// </summary>
internal sealed class PointerDerefIndexPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly PointerDerefIndexPreprocessor _preprocessor;
    private readonly List<PointerDerefIndexDiagnostic> _diagnostics = new();
    private readonly PointerDerefIndexStatistics _statistics = new();

    public PointerDerefIndexPassAdapter(
        PointerDerefIndexOptions options,
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
        _preprocessor = new PointerDerefIndexPreprocessor(options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.PointerDerefIndex);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "pointer-deref-index-preprocess";

    public PointerDerefIndexStatistics Statistics => _statistics;
    public IReadOnlyList<PointerDerefIndexDiagnostic> Diagnostics => _diagnostics;

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
                $"[pointer-deref-index] {(result.Changed ? "transformed" : "clean (copy)")}: " +
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
        => _diagnostics.Add(new PointerDerefIndexDiagnostic(
            PointerDerefIndexSeverity.Warning,
            PointerDerefIndexDiagnosticCode.PDI010,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new PointerDerefIndexDiagnostic(
            PointerDerefIndexSeverity.Error,
            PointerDerefIndexDiagnosticCode.PDI010,
            $"dotnet build failed with exit code {exitCode} after pointer-deref-index " +
            "preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new PointerDerefIndexDiagnostic(
            PointerDerefIndexSeverity.Error,
            PointerDerefIndexDiagnosticCode.PDI010,
            $"dotnet test failed with exit code {exitCode} after pointer-deref-index " +
            "preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Severity == PointerDerefIndexSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"pointer-deref-index: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.PointerDerefsRewritten} deref(s) rewritten, " +
           $"{_statistics.PointerDerefsPreserved} preserved (PDI001)";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != PointerDerefIndexSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private void MergeStatistics(PointerDerefIndexStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.PointerDerefsRewritten += source.PointerDerefsRewritten;
        _statistics.PointerDerefsPreserved += source.PointerDerefsPreserved;
    }
}
