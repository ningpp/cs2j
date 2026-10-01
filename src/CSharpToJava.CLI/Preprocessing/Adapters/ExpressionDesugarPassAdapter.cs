using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the expression-desugar family pass. M1 drives the C36
/// collection-expression pipeline: analyze (per-site plans/diagnostics; strict
/// errors atomically preserve the file), bottom-up rewrite, family
/// TempLifter statement insertion, CE-12 residual gate and formatting. No
/// support library, no AfterFiles project references.
/// </summary>
internal sealed class ExpressionDesugarPassAdapter : ProjectPassAdapter
{
    private readonly ExpressionDesugarOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly ExpressionDesugarPreprocessor _preprocessor = new();
    private readonly List<ExpressionDesugarDiagnostic> _diagnostics = new();
    private readonly ExpressionDesugarStatistics _statistics = new();

    public ExpressionDesugarPassAdapter(
        ExpressionDesugarOptions options,
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

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.ExpressionDesugar);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;

    public ExpressionDesugarStatistics Statistics => _statistics;
    public IReadOnlyList<ExpressionDesugarDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(
            context.SourceCode, context.SemanticModel, _options);
        _statistics.MergeFrom(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[expression-desugar] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            return Task.FromResult<PassFileRewrite?>(new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
            });
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ExpressionDesugarDiagnostic(
            ExpressionDesugarSeverity.Warning,
            CollectionExprDiagnosticCodes.CS2J0571,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ExpressionDesugarDiagnostic(
            ExpressionDesugarSeverity.Error,
            CollectionExprDiagnosticCodes.CS2J0589,
            "dotnet build failed with exit code " +
            $"{exitCode} after expression-desugar preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ExpressionDesugarDiagnostic(
            ExpressionDesugarSeverity.Error,
            CollectionExprDiagnosticCodes.CS2J0589,
            "dotnet test failed with exit code " +
            $"{exitCode} after expression-desugar preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == ExpressionDesugarSeverity.Error);

    public override string BuildChainSummaryLine()
    {
        var c = _statistics.CollectionExpr;
        var skipped = c.SkippedByReason.Values.Sum();
        return $"expression-desugar: {_statistics.FilesTransformed} transformed, " +
               $"{_statistics.FilesSkippedClean} clean, " +
               $"{_statistics.FilesDirtyNoRewrite} dirty-no-rewrite; " +
               $"coll: {c.CollectionSites} sites " +
               $"(array {c.ByTargetArray}/mutable {c.ByTargetMutableConcrete}" +
               $"/iface {c.ByTargetInterface}/span {c.ByTargetSpan}" +
               $"/builder {c.ByTargetCollectionBuilder}/unknown {c.ByTargetUnknown}), " +
               $"spread {c.SpreadElements} " +
               $"(addrange {c.SpreadAddRange}/span-foreach {c.SpreadSpanForeach}" +
               $"/enum-foreach {c.SpreadEnumerableForeach}), " +
               $"empty {c.EmptySites}, nested {c.NestedSites}, " +
               $"lifted {c.StatementLiftedSites}, skipped {skipped}, " +
               $"residual {c.ResidualCollectionCount}";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics.Select(d =>
        {
            var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
            return $"[expression-desugar] [{d.Severity}] {prefix}{d.Code}: {d.Message}";
        }).ToList();
}
