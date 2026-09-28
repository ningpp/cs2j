using CSharpToJava.Core.LocalFunctionLift;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CoreLocalFunctionLiftOptions = CSharpToJava.Core.LocalFunctionLift.LocalFunctionLiftOptions;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>Adapter for the local-function lift pass (former ProjectLocalFunctionLiftPreprocessor).</summary>
internal sealed class LocalFunctionLiftPassAdapter : ProjectPassAdapter
{
    private readonly CoreLocalFunctionLiftOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly LocalFunctionLiftPreprocessor _preprocessor = new();
    private readonly List<LocalFunctionLiftDiagnostic> _diagnostics = new();
    private readonly LocalFunctionLiftStatistics _statistics = new();

    public LocalFunctionLiftPassAdapter(
        CoreLocalFunctionLiftOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        bool strict)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _strict = strict;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.LocalFunctionLift);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public LocalFunctionLiftStatistics Statistics => _statistics;
    public IReadOnlyList<LocalFunctionLiftDiagnostic> Diagnostics => _diagnostics;

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
            Console.WriteLine(
                $"[local-fn] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        var residualCount = CountResidualLocalFunctions(tree.IntermediateRoot);
        _statistics.LocalFunctionsResidual = residualCount;

        if (residualCount > 0)
        {
            var severity = _strict
                ? LocalFunctionLiftSeverity.Error
                : LocalFunctionLiftSeverity.Warning;
            _diagnostics.Add(new LocalFunctionLiftDiagnostic(
                severity,
                $"{residualCount} residual local function(s) remaining after preprocessing.",
                null, 0, LocalFunctionSkipReason.None));
        }

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after local function lift."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after local function lift."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == LocalFunctionLiftSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"local-function-lift: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.LocalFunctionsLifted} lifted, " +
           $"{_statistics.LocalFunctionsSkipped} skipped, " +
           $"{_statistics.LocalFunctionsResidual} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d => $"[{d.Severity}] {d.FilePath}:{d.Line}: {d.Message}")
            .ToList();

    private static int CountResidualLocalFunctions(string intermediateRoot)
    {
        var count = 0;
        foreach (var csFile in Directory.EnumerateFiles(
                     intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csFile);
            var root = CSharpSyntaxTree.ParseText(content).GetRoot();
            count += root.DescendantNodes()
                .OfType<LocalFunctionStatementSyntax>().Count();
        }

        return count;
    }

    private void MergeStatistics(LocalFunctionLiftStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.LocalFunctionsLifted += source.LocalFunctionsLifted;
        _statistics.LocalFunctionsSkipped += source.LocalFunctionsSkipped;
        _statistics.CapturesByValue += source.CapturesByValue;
        _statistics.CapturesByRef += source.CapturesByRef;
        foreach (var kvp in source.SkippedByReason)
        {
            if (!_statistics.SkippedByReason.TryAdd(kvp.Key, kvp.Value))
            {
                _statistics.SkippedByReason[kvp.Key] += kvp.Value;
            }
        }
    }
}
