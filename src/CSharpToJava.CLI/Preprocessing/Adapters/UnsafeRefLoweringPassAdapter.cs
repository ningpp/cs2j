using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.UnsafeRefLowering;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the independent <c>unsafe-ref-lowering</c> ABI pass. It uses the
/// standard <see cref="ProjectPassAdapter"/> spine (semantic model required) and
/// rewrites each file through <see cref="UnsafeRefLoweringPreprocessor"/>:
/// <c>ref T x = ref MemoryMarshal.GetReference(span)</c> declarations whose
/// references are only <c>Unsafe.ReadUnaligned</c>/<c>Unsafe.Add</c> value reads
/// are lowered to direct span reads. Unprovable locals are preserved with URL001.
/// There is no support library and no generated file.
/// </summary>
internal sealed class UnsafeRefLoweringPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly UnsafeRefLoweringPreprocessor _preprocessor;
    private readonly List<UnsafeRefLoweringDiagnostic> _diagnostics = new();
    private readonly UnsafeRefLoweringStatistics _statistics = new();

    public UnsafeRefLoweringPassAdapter(
        UnsafeRefLoweringOptions options,
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
        _preprocessor = new UnsafeRefLoweringPreprocessor(options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.UnsafeRefLowering);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "unsafe-ref-lowering-preprocess";

    public UnsafeRefLoweringStatistics Statistics => _statistics;
    public IReadOnlyList<UnsafeRefLoweringDiagnostic> Diagnostics => _diagnostics;

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
                $"[unsafe-ref-lowering] {(result.Changed ? "transformed" : "clean (copy)")}: " +
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
        => _diagnostics.Add(new UnsafeRefLoweringDiagnostic(
            UnsafeRefLoweringSeverity.Warning,
            UnsafeRefLoweringDiagnosticCode.URL010,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new UnsafeRefLoweringDiagnostic(
            UnsafeRefLoweringSeverity.Error,
            UnsafeRefLoweringDiagnosticCode.URL010,
            $"dotnet build failed with exit code {exitCode} after unsafe-ref-lowering " +
            "preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new UnsafeRefLoweringDiagnostic(
            UnsafeRefLoweringSeverity.Error,
            UnsafeRefLoweringDiagnosticCode.URL010,
            $"dotnet test failed with exit code {exitCode} after unsafe-ref-lowering " +
            "preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Severity == UnsafeRefLoweringSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"unsafe-ref-lowering: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.RefLocalsLowered} ref local(s) lowered " +
           $"({_statistics.ReadUnalignedSitesLowered} read, " +
           $"{_statistics.AddValueSitesLowered} add), " +
           $"{_statistics.RejectedRefLocals} rejected (URL001)";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != UnsafeRefLoweringSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private void MergeStatistics(UnsafeRefLoweringStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.RefLocalsLowered += source.RefLocalsLowered;
        _statistics.ReadUnalignedSitesLowered += source.ReadUnalignedSitesLowered;
        _statistics.AddValueSitesLowered += source.AddValueSitesLowered;
        _statistics.RejectedRefLocals += source.RejectedRefLocals;
    }
}
