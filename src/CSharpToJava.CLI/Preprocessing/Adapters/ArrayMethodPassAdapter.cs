using CSharpToJava.Core.ArrayMethodRewriter;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.CLI.Preprocessing.SupportLibraries;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the static array-helper / multi-dimensional array rewrite pass.
/// Preserves the exact statistics merge, own-release-library exclusion,
/// Cs2jArray injection/project-reference wiring, BOM handling and diagnostic
/// wording of the former ProjectArrayMethodPreprocessor.
/// </summary>
internal sealed class ArrayMethodPassAdapter : ProjectPassAdapter
{
    private readonly ArrayMethodRewriterOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly ArrayMethodRewriter _rewriter = new();
    private readonly List<ArrayMethodRewriterDiagnostic> _diagnostics = new();
    private readonly ArrayMethodRewriterStatistics _statistics = new();
    private readonly List<string> _rewrittenSourceFiles = new();
    private List<string> _ownReleaseDirectories = new();

    public ArrayMethodPassAdapter(
        ArrayMethodRewriterOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.ArrayMethod);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree) => new();

    public ArrayMethodRewriterStatistics Statistics => _statistics;
    public IReadOnlyList<ArrayMethodRewriterDiagnostic> Diagnostics => _diagnostics;

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
                Console.WriteLine($"[array-method] clean (own release copy): {context.RelativePath}");
            }

            return Task.FromResult<PassFileRewrite?>(null);
        }

        var result = _rewriter.Rewrite(context.SourceCode, context.SemanticModel!, _options);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { Message = $"{context.RelativePath}: {diagnostic.Message}" });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[array-method] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            _rewrittenSourceFiles.Add(context.SourceFilePath);
            var rewrite = new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            };
            return Task.FromResult<PassFileRewrite?>(rewrite);
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_statistics.InstanceMemberRewrites > 0 || _statistics.StaticArrayCalls > 0
            || _statistics.ResizeStatements > 0 || _statistics.ForeachFlattened > 0
            || _statistics.RankInlined > 0 || _statistics.MultiDimLengthRewrites > 0)
        {
            await Cs2jArraySupportLibrary.EnsureSupportAsync(
                tree, _rewrittenSourceFiles, AddTfmWarning);
        }
    }

    private void AddTfmWarning(string projectFilePath, string message)
        => _diagnostics.Add(new ArrayMethodRewriterDiagnostic(
            ArrayMethodSeverity.Warning, $"{projectFilePath}: {message}"));

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ArrayMethodRewriterDiagnostic(
            ArrayMethodSeverity.Error,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved, leading to incorrect code generation."));

    public override void OnBuildFailed(int exitCode)
    {
        // Array-method historically does not run verify builds.
    }

    public override void OnTestsFailed(int exitCode)
    {
        // Array-method historically does not run verify tests.
    }

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ArrayMethodSeverity.Error);

    /// <summary>The convert-project chain printed no array-method summary line.</summary>
    public override string? BuildChainSummaryLine() => null;

    public override string? BuildChainStartLine()
        => _verbose ? "[convert-project] Running array-method preprocessor..." : null;

    public override string? BuildChainVerboseSummaryLine()
        => _verbose
            ? $"[convert-project] array-method: {_statistics.FilesTransformed} transformed, " +
              $"{_statistics.FilesSkippedClean} clean"
            : null;

    public override string? BuildChainFailureBanner()
        => "[convert-project] array-method preprocessing failed; aborting.";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Select(d => $"[array-method] [{d.Severity}] {d.Message}")
            .ToList();

    private void MergeStatistics(ArrayMethodRewriterStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.InstanceMemberRewrites += source.InstanceMemberRewrites;
        _statistics.StaticArrayCalls += source.StaticArrayCalls;
        _statistics.ResizeStatements += source.ResizeStatements;
        _statistics.ForeachFlattened += source.ForeachFlattened;
        _statistics.RankInlined += source.RankInlined;
        _statistics.MultiDimLengthRewrites += source.MultiDimLengthRewrites;
    }
}
