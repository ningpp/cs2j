using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.StackAllocLowering;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the independent <c>stackalloc-lowering</c> post-pass. It uses the
/// standard <see cref="ProjectPassAdapter"/> spine (semantic model required) and
/// rewrites each file through <see cref="StackAllocLoweringPreprocessor"/>:
/// residual Span/ReadOnlySpan-target stackalloc sites and the provable
/// "pointer local + Span construction" shape are lowered to heap arrays, while
/// every unprovable bare-pointer site is preserved with an SL001 warning.
/// There is no support library and no generated file.
/// </summary>
internal sealed class StackAllocLoweringPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly StackAllocLoweringPreprocessor _preprocessor;
    private readonly List<StackAllocLoweringDiagnostic> _diagnostics = new();
    private readonly StackAllocLoweringStatistics _statistics = new();

    public StackAllocLoweringPassAdapter(
        StackAllocLoweringOptions options,
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
        _preprocessor = new StackAllocLoweringPreprocessor(options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.StackAllocLowering);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "stackalloc-lowering-preprocess";

    public StackAllocLoweringStatistics Statistics => _statistics;
    public IReadOnlyList<StackAllocLoweringDiagnostic> Diagnostics => _diagnostics;

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
                $"[stackalloc-lowering] {(result.Changed ? "transformed" : "clean (copy)")}: " +
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
        => _diagnostics.Add(new StackAllocLoweringDiagnostic(
            StackAllocLoweringSeverity.Warning,
            StackAllocLoweringDiagnosticCode.SL010,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new StackAllocLoweringDiagnostic(
            StackAllocLoweringSeverity.Error,
            StackAllocLoweringDiagnosticCode.SL010,
            $"dotnet build failed with exit code {exitCode} after stackalloc-lowering " +
            "preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new StackAllocLoweringDiagnostic(
            StackAllocLoweringSeverity.Error,
            StackAllocLoweringDiagnosticCode.SL010,
            $"dotnet test failed with exit code {exitCode} after stackalloc-lowering " +
            "preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Severity == StackAllocLoweringSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"stackalloc-lowering: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.StackAllocSitesLowered} site(s) lowered " +
           $"({_statistics.SpanTargetSitesLowered} span, " +
           $"{_statistics.PointerLocalSitesLowered} pointer local, " +
           $"{_statistics.SpanConstructionsRewritten} span construction(s) rewritten), " +
           $"{_statistics.UnsafeBlocksUnwrapped} unsafe block(s) unwrapped, " +
           $"{_statistics.RejectedSites} rejected (SL001)";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != StackAllocLoweringSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private void MergeStatistics(StackAllocLoweringStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.SpanTargetSitesLowered += source.SpanTargetSitesLowered;
        _statistics.PointerLocalSitesLowered += source.PointerLocalSitesLowered;
        _statistics.SpanConstructionsRewritten += source.SpanConstructionsRewritten;
        _statistics.UnsafeBlocksUnwrapped += source.UnsafeBlocksUnwrapped;
        _statistics.RejectedSites += source.RejectedSites;
    }
}
