using CSharpToJava.Core.FixedSpanToArray;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the independent <c>fixed-span-to-array</c> ABI pass. It uses the
/// standard <see cref="ProjectPassAdapter"/> spine (semantic model required) and
/// rewrites each file through <see cref="FixedSpanToArrayPreprocessor"/>:
/// <c>fixed (T* p = span)</c> and
/// <c>fixed (T* p = &amp;MemoryMarshal.GetReference(span))</c> become fixed over a
/// materialized array, with a try/finally write-back for writable spans. Every
/// other fixed source is preserved for <c>unsafe-ffm</c>; unprovable span shapes
/// stay verbatim with FSA001. There is no support library and no generated file.
/// </summary>
internal sealed class FixedSpanToArrayPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly FixedSpanToArrayPreprocessor _preprocessor;
    private readonly List<FixedSpanToArrayDiagnostic> _diagnostics = new();
    private readonly FixedSpanToArrayStatistics _statistics = new();

    public FixedSpanToArrayPassAdapter(
        FixedSpanToArrayOptions options,
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
        _preprocessor = new FixedSpanToArrayPreprocessor(options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.FixedSpanToArray);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "fixed-span-to-array-preprocess";

    public FixedSpanToArrayStatistics Statistics => _statistics;
    public IReadOnlyList<FixedSpanToArrayDiagnostic> Diagnostics => _diagnostics;

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
                $"[fixed-span-to-array] {(result.Changed ? "transformed" : "clean (copy)")}: " +
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
        => _diagnostics.Add(new FixedSpanToArrayDiagnostic(
            FixedSpanToArraySeverity.Warning,
            FixedSpanToArrayDiagnosticCode.FSA010,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new FixedSpanToArrayDiagnostic(
            FixedSpanToArraySeverity.Error,
            FixedSpanToArrayDiagnosticCode.FSA010,
            $"dotnet build failed with exit code {exitCode} after fixed-span-to-array " +
            "preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new FixedSpanToArrayDiagnostic(
            FixedSpanToArraySeverity.Error,
            FixedSpanToArrayDiagnosticCode.FSA010,
            $"dotnet test failed with exit code {exitCode} after fixed-span-to-array " +
            "preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Severity == FixedSpanToArraySeverity.Error);

    public override string BuildChainSummaryLine()
        => $"fixed-span-to-array: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.SpanFixedStatementsLowered} span fixed statement(s) lowered, " +
           $"{_statistics.SpanFixedStatementsPreserved} preserved (FSA001)";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != FixedSpanToArraySeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private void MergeStatistics(FixedSpanToArrayStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.SpanFixedStatementsLowered += source.SpanFixedStatementsLowered;
        _statistics.SpanFixedStatementsPreserved += source.SpanFixedStatementsPreserved;
    }
}
