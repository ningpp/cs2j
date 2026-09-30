using CSharpToJava.Core.IndexRange;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.CLI.Preprocessing.SupportLibraries;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C31 index-range (<c>^</c>/<c>..</c>) rewrite pass.
/// Wraps <see cref="IndexRangePreprocessor"/> with the Cs2jArray
/// own-release exclusion, support-library release/project-reference wiring
/// and a whole-intermediate-tree residual rescan.
/// </summary>
internal sealed class IndexRangePassAdapter : ProjectPassAdapter
{
    private readonly IndexRangeOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly IndexRangePreprocessor _preprocessor = new();
    private readonly List<IndexRangeDiagnostic> _diagnostics = new();
    private readonly IndexRangeStatistics _statistics = new();
    private readonly List<string> _rewrittenSourceFiles = new();
    private List<string> _ownReleaseDirectories = new();
    private int _expectedRemaining;

    public IndexRangePassAdapter(
        IndexRangeOptions options,
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

    public const string IntermediateDirectoryName = ".cs2j-index-range-src";

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.IndexRange);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public IndexRangeStatistics Statistics => _statistics;
    public IReadOnlyList<IndexRangeDiagnostic> Diagnostics => _diagnostics;

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _ownReleaseDirectories =
            Cs2jArraySupportLibrary.FindOwnReleaseDirectories(tree.SourceRoot);
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        if (Cs2jArraySupportLibrary.IsUnderOwnRelease(
                context.SourceFilePath, _ownReleaseDirectories))
        {
            if (_verbose)
            {
                Console.WriteLine(
                    $"[index-range] clean (own release copy): {context.RelativePath}");
            }

            return Task.FromResult<PassFileRewrite?>(null);
        }

        var result = _preprocessor.Preprocess(
            context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (!result.Changed)
        {
            // Dirty but diagnostics-only: those ^/.. nodes legitimately remain.
            _expectedRemaining += CountIndexRangeExpressions(context.SourceCode);
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[index-range] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            _rewrittenSourceFiles.Add(context.SourceFilePath);
            return Task.FromResult<PassFileRewrite?>(new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
            });
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_rewrittenSourceFiles.Count > 0)
        {
            await Cs2jArraySupportLibrary.EnsureSupportAsync(
                tree, _rewrittenSourceFiles, AddTfmWarning);
        }

        var remaining = CountIndexRangeExpressionsInTree(tree.IntermediateRoot);
        _statistics.ResidualCount = Math.Max(0, remaining - _expectedRemaining);

        if (remaining > _expectedRemaining)
        {
            var severity = _strict
                ? IndexRangeSeverity.Error
                : IndexRangeSeverity.Warning;
            _diagnostics.Add(new IndexRangeDiagnostic(
                "IR140",
                severity,
                $"{remaining - _expectedRemaining} residual ^/.. expression(s) " +
                "remaining in the intermediate tree."));
        }
    }

    private void AddTfmWarning(string projectFilePath, string message)
        => _diagnostics.Add(new IndexRangeDiagnostic(
            "IR201",
            IndexRangeSeverity.Warning,
            $"{projectFilePath}: {message}"));

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new IndexRangeDiagnostic(
            "IR201",
            IndexRangeSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new IndexRangeDiagnostic(
            "IR140",
            IndexRangeSeverity.Error,
            $"dotnet build failed with exit code {exitCode} after index-range preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new IndexRangeDiagnostic(
            "IR140",
            IndexRangeSeverity.Error,
            $"dotnet test failed with exit code {exitCode} after index-range preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == IndexRangeSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"index-range: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.HatElementSites} hat sites, " +
           $"{_statistics.RangeSliceSites} slice sites, " +
           $"{_statistics.ReceiverTempsLifted + _statistics.BoundTempsLifted} temps, " +
           $"{_statistics.StandaloneIndexSites + _statistics.StandaloneRangeSites} standalone, " +
           $"{_statistics.ResidualCount} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics.Select(d =>
        {
            var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
            return $"[index-range] [{d.Severity}] {prefix}{d.Message}";
        }).ToList();

    private static int CountIndexRangeExpressions(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return CountIndexRangeExpressions(root);
    }

    private static int CountIndexRangeExpressions(SyntaxNode root)
        => root.DescendantNodesAndSelf()
            .Count(n =>
                n.RawKind == (int)SyntaxKind.IndexExpression
                || n.RawKind == (int)SyntaxKind.RangeExpression);

    private static int CountIndexRangeExpressionsInTree(string intermediateRoot)
    {
        var count = 0;
        foreach (var csFile in Directory.EnumerateFiles(
                     intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(csFile)).GetRoot();
            count += CountIndexRangeExpressions(root);
        }

        return count;
    }

    private void MergeStatistics(IndexRangeStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        _statistics.HatElementSites += source.HatElementSites;
        _statistics.RangeSliceSites += source.RangeSliceSites;
        _statistics.StandaloneIndexSites += source.StandaloneIndexSites;
        _statistics.StandaloneRangeSites += source.StandaloneRangeSites;
        _statistics.ReceiverTempsLifted += source.ReceiverTempsLifted;
        _statistics.BoundTempsLifted += source.BoundTempsLifted;
        _statistics.LengthTempReads += source.LengthTempReads;
        _statistics.ArrayHelperCalls += source.ArrayHelperCalls;
        _statistics.StringSpecializations += source.StringSpecializations;
        foreach (var pair in source.SkippedByReason)
        {
            _statistics.SkippedByReason.TryGetValue(pair.Key, out var count);
            _statistics.SkippedByReason[pair.Key] = count + pair.Value;
        }
    }
}
