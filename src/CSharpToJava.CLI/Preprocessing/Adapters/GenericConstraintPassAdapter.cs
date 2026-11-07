using CSharpToJava.Core.GenericConstraint;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the <c>generic-constraint</c> (R2/CN) pass: whole-tree analyze
/// once (CN-12 dependent-site facts), per-file CN rewrite, strict file-level
/// rollback on any Error diagnostic.
/// </summary>
internal sealed class GenericConstraintPassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-generic-constraint-src";

    private readonly GenericConstraintOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly GenericConstraintPreprocessor _preprocessor = new();
    private readonly List<GenericConstraintDiagnostic> _diagnostics = new();
    private readonly GenericConstraintStatistics _statistics = new();

    public GenericConstraintPassAdapter(
        GenericConstraintOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.GenericConstraint);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public GenericConstraintStatistics Statistics => _statistics;
    public IReadOnlyList<GenericConstraintDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        _preprocessor.BindCompilation(compilation);
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.RewriteFile(context.SemanticModel!, _options);
        _statistics.Merge(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[generic-constraint] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new GenericConstraintDiagnostic(
            GenericConstraintSeverity.Warning,
            GenericConstraintRule.Infrastructure,
            GenericConstraintCodes.ConstraintNotExpressible,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new GenericConstraintDiagnostic(
            GenericConstraintSeverity.Error,
            GenericConstraintRule.Infrastructure,
            GenericConstraintCodes.ConstraintNotExpressible,
            $"dotnet build failed with exit code {exitCode} after generic-constraint preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new GenericConstraintDiagnostic(
            GenericConstraintSeverity.Error,
            GenericConstraintRule.Infrastructure,
            GenericConstraintCodes.ConstraintNotExpressible,
            $"dotnet test failed with exit code {exitCode} after generic-constraint preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(diagnostic => diagnostic.Severity == GenericConstraintSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(diagnostic => diagnostic.Severity != GenericConstraintSeverity.Info || verbose)
            .Select(diagnostic => $"[{diagnostic.Severity}] {diagnostic.FilePath}: {diagnostic.Message}")
            .ToList();
}
