using CSharpToJava.Core.GenericPreprocess;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CoreGenericPreprocessOptions = CSharpToJava.Core.GenericPreprocess.GenericPreprocessOptions;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>Adapter for the generic erasure-deepening pass (former ProjectGenericPreprocessor).</summary>
internal sealed class GenericPassAdapter : ProjectPassAdapter
{
    private readonly CoreGenericPreprocessOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly GenericPreprocessor _preprocessor = new();
    private readonly List<GenericPreprocessDiagnostic> _diagnostics = new();
    private readonly GenericPreprocessStatistics _statistics = new();

    public GenericPassAdapter(
        CoreGenericPreprocessOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.Generic);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public GenericPreprocessStatistics Statistics => _statistics;
    public IReadOnlyList<GenericPreprocessDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        if (_options.SyncTypeOfForOverrideSiblings)
        {
            _preprocessor.PrescanOverrideSiblings(compilation);
        }

        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine($"[generic] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new GenericPreprocessDiagnostic(
            GenericPreprocessSeverity.Warning,
            GenericPattern.DefaultExpression,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved, leading to incorrect code generation."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new GenericPreprocessDiagnostic(
            GenericPreprocessSeverity.Error,
            GenericPattern.DefaultExpression,
            $"dotnet build failed with exit code {exitCode} after generic preprocessing. " +
            "The generic rewrite produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new GenericPreprocessDiagnostic(
            GenericPreprocessSeverity.Error,
            GenericPattern.DefaultExpression,
            $"dotnet test failed with exit code {exitCode} after generic preprocessing."));

    public override void ReportVerifyCommandResult(string verb, string targetPath, int exitCode)
    {
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"dotnet {verb} exited with code {exitCode} for {targetPath}");
        }
        else
        {
            Console.WriteLine($"[generic] dotnet {verb} succeeded for {targetPath}");
        }
    }

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == GenericPreprocessSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"generic: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean " +
           $"({_statistics.DefaultExpressionsRewritten} default(T), " +
           $"{_statistics.NewTypeParamRewritten} new T(), " +
           $"{_statistics.NewTypeParamArrayRewritten} new T[n])";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != GenericPreprocessSeverity.Info || verbose)
            .Select(d => $"[{d.Severity}] {d.FilePath}: {d.Message}")
            .ToList();

    private void MergeStatistics(GenericPreprocessStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.DefaultExpressionsRewritten += source.DefaultExpressionsRewritten;
        _statistics.NewTypeParamRewritten += source.NewTypeParamRewritten;
        _statistics.NewTypeParamArrayRewritten += source.NewTypeParamArrayRewritten;
        _statistics.TypeOfExpressionsSeen += source.TypeOfExpressionsSeen;
        _statistics.IsTypeParamSeen += source.IsTypeParamSeen;
        _statistics.AsTypeParamSeen += source.AsTypeParamSeen;
    }
}
