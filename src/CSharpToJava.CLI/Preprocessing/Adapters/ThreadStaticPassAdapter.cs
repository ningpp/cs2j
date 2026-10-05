using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ThreadStaticRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the thread-static (C09) pass: whole-tree Bind+Analyze once,
/// per-file rewrite, and the post-rewrite residual gate that reconciles
/// remaining [ThreadStatic] attributes against the skipped plan set
/// (design §4.2/§5.1). Mirrors GenericDeepenPassAdapter's lifecycle.
/// </summary>
internal sealed class ThreadStaticPassAdapter : ProjectPassAdapter
{
    public const string IntermediateDirectoryName = ".cs2j-thread-static-src";

    private readonly ThreadStaticOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly ThreadStaticPreprocessor _preprocessor = new();
    private readonly List<ThreadStaticDiagnostic> _diagnostics = new();
    private readonly ThreadStaticStatistics _statistics = new();

    public ThreadStaticPassAdapter(
        ThreadStaticOptions options,
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

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.ThreadStatic);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public ThreadStaticStatistics Statistics => _statistics;
    public IReadOnlyList<ThreadStaticDiagnostic> Diagnostics => _diagnostics;

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
            Console.WriteLine($"[thread-static] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        RunResidualScan(tree);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Post-rewrite gate (design §4.2 step 4 / §7.4): recompile the
    /// intermediate tree, count the remaining ThreadStaticAttribute instances
    /// and reconcile them against the inactive plans. Every skipped plan
    /// (CS2J1201/1202/1203/1204/1205/1290) leaves its declaration untouched,
    /// so exactly one attribute per skipped field is expected to remain.
    /// Attributes beyond that registered skip set mean the rewrite dropped a
    /// field; each such residual is reported CS2J1220 (Warning, Error under
    /// --strict).
    /// </summary>
    private void RunResidualScan(PassTreeContext tree)
    {
        var parseOptions = CreateParseOptions(tree);
        var trees = new List<SyntaxTree>();
        foreach (var file in Directory.EnumerateFiles(tree.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, path: file));
        }

        if (trees.Count == 0)
        {
            return;
        }

        var compilation = CSharpCompilation.Create(
            CompilationName,
            trees, GetMetadataReferences(), CreateCompilationOptions());

        var (residualCount, locations) = _preprocessor.ResidualScan(compilation);
        _statistics.ResidualThreadStaticAttributes = residualCount;

        var expectedResidual = _preprocessor.Plans.Count(plan => !plan.Active);
        var unexpected = residualCount - expectedResidual;
        if (unexpected <= 0)
        {
            return;
        }

        var severity = _options.Strict
            ? ThreadStaticSeverity.Error
            : ThreadStaticSeverity.Warning;

        var orderedLocations = locations
            .OrderBy(location => location.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(location => location.SourceSpan.Start)
            .ToList();

        // The scanner cannot attribute residuals to plans on its own; list all
        // residual sites (deterministic order) in the aggregated message.
        var siteList = string.Join("; ", orderedLocations.Select(location =>
        {
            var lineSpan = location.GetLineSpan();
            return $"{lineSpan.Path}({lineSpan.StartLinePosition.Line + 1})";
        }));

        _diagnostics.Add(new ThreadStaticDiagnostic(
            severity,
            ThreadStaticRule.Residual,
            ThreadStaticCodes.ResidualFound,
            $"{unexpected} residual [ThreadStatic] attribute(s) remain after thread-static with no matching skipped plan "
                + $"(expected {expectedResidual} registered skip(s), found {residualCount}); the rewrite did not cover them. Sites: {siteList}",
            tree.IntermediateRoot));
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ThreadStaticDiagnostic(
            ThreadStaticSeverity.Warning,
            ThreadStaticRule.Infrastructure,
            ThreadStaticCodes.RestoreOrBuildFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ThreadStaticDiagnostic(
            ThreadStaticSeverity.Error,
            ThreadStaticRule.Infrastructure,
            ThreadStaticCodes.RestoreOrBuildFailed,
            $"dotnet build failed with exit code {exitCode} after thread-static preprocessing; the rewrite produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ThreadStaticDiagnostic(
            ThreadStaticSeverity.Error,
            ThreadStaticRule.Infrastructure,
            ThreadStaticCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after thread-static preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ThreadStaticSeverity.Error);

    public override string BuildChainSummaryLine() => _statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != ThreadStaticSeverity.Info || verbose)
            .Select(d => d.ToString())
            .ToList();
}
