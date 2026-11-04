using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.UsingResourceLift;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the independent <c>using-resource-lift</c> control-flow pass. It
/// uses the standard <see cref="ProjectPassAdapter"/> spine (semantic model
/// required) and rewrites each file through
/// <see cref="UsingResourceLiftPreprocessor"/>: a <c>using</c> resource
/// expression that contains a <c>ref</c>/<c>out</c> read-back (or a
/// <c>ByRef&lt;T&gt;</c> factory call) is lifted into a preceding ordinary local
/// declaration so the Java statement gate sees a plain identifier resource.
/// There is no support library and no generated file.
/// </summary>
internal sealed class UsingResourceLiftPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly UsingResourceLiftPreprocessor _preprocessor;
    private readonly List<UsingResourceLiftDiagnostic> _diagnostics = new();
    private readonly UsingResourceLiftStatistics _statistics = new();

    public UsingResourceLiftPassAdapter(
        UsingResourceLiftOptions options,
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
        _preprocessor = new UsingResourceLiftPreprocessor(options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.UsingResourceLift);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "using-resource-lift-preprocess";

    public UsingResourceLiftStatistics Statistics => _statistics;
    public IReadOnlyList<UsingResourceLiftDiagnostic> Diagnostics => _diagnostics;

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
                $"[using-resource-lift] {(result.Changed ? "transformed" : "clean (copy)")}: " +
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
        => _diagnostics.Add(new UsingResourceLiftDiagnostic(
            UsingResourceLiftSeverity.Warning,
            UsingResourceLiftDiagnosticCode.USL010,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new UsingResourceLiftDiagnostic(
            UsingResourceLiftSeverity.Error,
            UsingResourceLiftDiagnosticCode.USL010,
            $"dotnet build failed with exit code {exitCode} after using-resource-lift " +
            "preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new UsingResourceLiftDiagnostic(
            UsingResourceLiftSeverity.Error,
            UsingResourceLiftDiagnosticCode.USL010,
            $"dotnet test failed with exit code {exitCode} after using-resource-lift " +
            "preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Severity == UsingResourceLiftSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"using-resource-lift: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.UsingResourcesLifted} resource(s) lifted, " +
           $"{_statistics.UsingResourcesSkipped} preserved (USL001)";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != UsingResourceLiftSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private void MergeStatistics(UsingResourceLiftStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.UsingResourcesLifted += source.UsingResourcesLifted;
        _statistics.UsingResourcesSkipped += source.UsingResourcesSkipped;
    }
}
