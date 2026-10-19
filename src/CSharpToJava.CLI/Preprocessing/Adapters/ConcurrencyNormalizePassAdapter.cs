using CSharpToJava.Core.ConcurrencyNormalize;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C29 concurrency-normalize pass. Semantic per-file rewrites
/// (EnumPassAdapter shape) plus a one-shot Bind/Analyze in
/// <see cref="CompilationReadyAsync"/> and an AfterFiles residual gate
/// (design §5.1). The shared <see cref="ConcurrencyStatistics"/> instance is
/// owned by this adapter and accumulated in place by the preprocessor.
/// </summary>
internal sealed class ConcurrencyNormalizePassAdapter : ProjectPassAdapter
{
    private readonly ConcurrencyOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly ConcurrencyPreprocessor _preprocessor = new();
    private readonly List<ConcurrencyDiagnostic> _diagnostics = new();
    private readonly ConcurrencyStatistics _statistics = new();

    public ConcurrencyNormalizePassAdapter(
        ConcurrencyOptions options,
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

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.ConcurrencyNormalize);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public ConcurrencyStatistics Statistics => _statistics;
    public IReadOnlyList<ConcurrencyDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        // One-shot whole-tree analyze; the baseline diagnostic snapshot
        // (CS1996/CS0185/CS0677 priority) is taken inside Analyze (§4.2 step 1).
        _preprocessor.BindCompilation(compilation);
        _preprocessor.Analyze();
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var fileDiagnostics = new List<ConcurrencyDiagnostic>();
        var result = _preprocessor.RewriteFile(
            context.SourceCode, context.SemanticModel!, _statistics, fileDiagnostics);

        foreach (var diagnostic in fileDiagnostics)
        {
            _diagnostics.Add(ApplyStrict(diagnostic with { FilePath = context.RelativePath }));
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[concurrency-normalize] {(result != null ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result == null
            ? null
            : new PassFileRewrite { OutputCode = result.OutputCode, Changed = true });
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // 性能：当没有任何文件被改写时，输出树与输入树逐字节一致，残余闸门
        // 重新编译 933 棵树并做全量 GetSymbolInfo 纯属重复分析阶段的工作
        //（画像 ~20s）。跳过它；分析阶段已枚举全部 Monitor 站点并归属 plan，
        // CS2J2970 残余诊断在 0 改写场景下不会产生新发现。
        if (_statistics.FilesTransformed > 0)
        {
            RunResidualScan(tree);
        }
        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ConcurrencyDiagnostic(
            ConcurrencySeverity.Error,
            ConcurrencyRule.CN2,
            ConcurrencyDiagnosticCodes.RestoreFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ConcurrencyDiagnostic(
            ConcurrencySeverity.Error,
            ConcurrencyRule.CN2,
            ConcurrencyDiagnosticCodes.BuildFailed,
            $"dotnet build failed with exit code {exitCode} after concurrency-normalize preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ConcurrencyDiagnostic(
            ConcurrencySeverity.Error,
            ConcurrencyRule.CN2,
            ConcurrencyDiagnosticCodes.TestFailed,
            $"dotnet test failed with exit code {exitCode} after concurrency-normalize preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ConcurrencySeverity.Error);

    public override string BuildChainSummaryLine()
        => $"concurrency-normalize: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.LockStatementsSeen} lock, " +
           $"{_statistics.EnterExitPairsRewritten} enter-exit, " +
           $"{_statistics.TryEnterSitesCanonicalized} try-enter, " +
           $"{_statistics.WaitSitesCanonicalized} wait, " +
           $"{_statistics.PulseSites + _statistics.PulseAllSites} pulse, " +
           $"{_statistics.SkippedByReason.Values.Sum()} skipped, " +
           $"{_statistics.ResidualMonitorCalls} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != ConcurrencySeverity.Info || verbose)
            .Select(d =>
            {
                var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
                return $"[{d.Severity} {d.Code}] {prefix}{d.Message}";
            })
            .ToList();

    /// <summary>
    /// Residual gate (§4.2 step 4): recompile the intermediate tree (output
    /// content under the source file paths so plan ownership matches) and
    /// reconcile every bound Monitor site against the plan set. Unknown
    /// residual sites are CS2J2970, escalated to Error under strict.
    /// </summary>
    private void RunResidualScan(PassTreeContext tree)
    {
        var parseOptions = CreateParseOptions(tree);
        var trees = new List<SyntaxTree>();
        foreach (var sourceFile in tree.CsFiles)
        {
            var intermediateFile = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.SourceRoot, sourceFile));
            if (!File.Exists(intermediateFile))
            {
                continue;
            }

            trees.Add(CSharpSyntaxTree.ParseText(
                File.ReadAllText(intermediateFile), parseOptions, path: sourceFile));
        }

        if (trees.Count == 0)
        {
            return;
        }

        var outputCompilation = CSharpCompilation.Create(
            CompilationName + "-residual",
            trees, GetMetadataReferences(), CreateCompilationOptions());

        foreach (var diagnostic in _preprocessor.ResidualScan(outputCompilation, _statistics))
        {
            var relativePath = diagnostic.FilePath == null
                ? null
                : Path.GetRelativePath(tree.SourceRoot, diagnostic.FilePath);
            _diagnostics.Add(ApplyStrict(diagnostic with { FilePath = relativePath }));
        }
    }

    /// <summary>Strict upgrades the gate/site diagnostics the design marks "Warning (strict Error)".</summary>
    private ConcurrencyDiagnostic ApplyStrict(ConcurrencyDiagnostic diagnostic)
    {
        if (!_strict
            || diagnostic.Severity != ConcurrencySeverity.Warning
            || diagnostic.Code is not (ConcurrencyDiagnosticCodes.NonCanonicalStructure
                or ConcurrencyDiagnosticCodes.TryEnterGate
                or ConcurrencyDiagnosticCodes.LockOwnershipUnproven
                or ConcurrencyDiagnosticCodes.WaitExitContext
                or ConcurrencyDiagnosticCodes.ResidualMonitorGate))
        {
            return diagnostic;
        }

        return diagnostic with { Severity = ConcurrencySeverity.Error };
    }
}
