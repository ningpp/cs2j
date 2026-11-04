using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.RefStructLowering;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the independent <c>ref-struct-lowering</c> post-pass. It uses the
/// standard <see cref="ProjectPassAdapter"/> spine (semantic model required) and
/// rewrites each file through <see cref="RefStructLoweringPreprocessor"/>:
/// user <c>ref struct</c> declarations without any ref-only member lose their
/// <c>ref</c> modifier, while every declaration with a pointer/ref/Span-shaped
/// member is preserved with an RSL001 warning for normalization-audit.
/// There is no support library and no generated file.
/// </summary>
internal sealed class RefStructLoweringPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly RefStructLoweringPreprocessor _preprocessor = new();
    private readonly List<RefStructLoweringDiagnostic> _diagnostics = new();
    private readonly RefStructLoweringStatistics _statistics = new();

    public RefStructLoweringPassAdapter(
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
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.RefStructLowering);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "ref-struct-lowering-preprocess";

    public RefStructLoweringStatistics Statistics => _statistics;
    public IReadOnlyList<RefStructLoweringDiagnostic> Diagnostics => _diagnostics;

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
                $"[ref-struct-lowering] {(result.Changed ? "transformed" : "clean (copy)")}: " +
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
        => _diagnostics.Add(new RefStructLoweringDiagnostic(
            RefStructLoweringSeverity.Warning,
            RefStructLoweringDiagnosticCode.RSL010,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new RefStructLoweringDiagnostic(
            RefStructLoweringSeverity.Error,
            RefStructLoweringDiagnosticCode.RSL010,
            $"dotnet build failed with exit code {exitCode} after ref-struct-lowering " +
            "preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new RefStructLoweringDiagnostic(
            RefStructLoweringSeverity.Error,
            RefStructLoweringDiagnosticCode.RSL010,
            $"dotnet test failed with exit code {exitCode} after ref-struct-lowering " +
            "preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Severity == RefStructLoweringSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"ref-struct-lowering: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.RefStructsLowered} ref struct(s) lowered, " +
           $"{_statistics.RefStructsPreserved} preserved (RSL001)";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != RefStructLoweringSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private void MergeStatistics(RefStructLoweringStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.RefStructsLowered += source.RefStructsLowered;
        _statistics.RefStructsPreserved += source.RefStructsPreserved;
    }
}
