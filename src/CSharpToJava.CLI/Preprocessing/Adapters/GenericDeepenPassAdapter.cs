using CSharpToJava.Core.GenericPreprocess;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the generic-deepen (C02) pass: whole-tree analyze once, per-file
/// rewrite, generated holder files distributed after all files (Tuple pattern).
/// </summary>
internal sealed class GenericDeepenPassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-generic-deepen-src";

    private readonly GenericDeepenOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly GenericDeepenPreprocessor _preprocessor = new();
    private readonly List<GenericDeepenDiagnostic> _diagnostics = new();
    private readonly GenericDeepenStatistics _statistics = new();

    public GenericDeepenPassAdapter(
        GenericDeepenOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.GenericDeepen);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public GenericDeepenStatistics Statistics => _statistics;
    public IReadOnlyList<GenericDeepenDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        _preprocessor.BindCompilation(compilation);
        _preprocessor.Analyze(_options);
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.RewriteFile(context.SemanticModel!);
        _statistics.Merge(result.Statistics);
        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine($"[generic-deepen] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Warning,
            GenericDeepenRule.Infrastructure,
            GenericDeepenCodes.RestoreOrBuildFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Error,
            GenericDeepenRule.Infrastructure,
            GenericDeepenCodes.RestoreOrBuildFailed,
            $"dotnet build failed with exit code {exitCode} after generic-deepen preprocessing; the rewrite produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new GenericDeepenDiagnostic(
            GenericDeepenSeverity.Error,
            GenericDeepenRule.Infrastructure,
            GenericDeepenCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after generic-deepen preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == GenericDeepenSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != GenericDeepenSeverity.Info || verbose)
            .Select(d => d.ToString())
            .ToList();
}
