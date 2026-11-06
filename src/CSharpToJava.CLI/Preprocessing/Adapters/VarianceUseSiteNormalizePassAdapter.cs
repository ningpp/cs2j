using System.Text;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.VarianceUseSite;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the chain-tail <c>variance-use-site-normalize</c> (VR) pass. It
/// uses the standard spine (whole-tree semantic model) and rewrites each file
/// through <see cref="VarianceUseSiteNormalizePreprocessor"/>, which reuses the
/// C33 audit inventory/classifier. The pass writes
/// <c>variance-use-site-normalize-report.json</c>; DeclaredUnsupported sites are
/// reported as CS2J1404 warnings, and a per-file validation failure rolls the
/// whole file back and surfaces as an Error.
/// </summary>
internal sealed class VarianceUseSiteNormalizePassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly VarianceUseSiteNormalizePreprocessor _preprocessor;
    private readonly List<VarianceUseSiteDiagnostic> _diagnostics = new();

    public VarianceUseSiteNormalizePassAdapter(
        VarianceUseSiteNormalizeOptions options,
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
        _preprocessor = new VarianceUseSiteNormalizePreprocessor(options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.VarianceUseSiteNormalize);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "variance-use-site-normalize-preprocess";

    public VarianceUseSiteNormalizeStatistics Statistics => _preprocessor.Statistics;
    public IReadOnlyList<VarianceUseSiteDiagnostic> Diagnostics => _diagnostics;

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        _preprocessor.BindCompilation(compilation);
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var model = context.SemanticModel
            ?? throw new InvalidOperationException(
                "variance-use-site-normalize requires a semantic model for every file.");
        var result = _preprocessor.RewriteFile(
            context.SourceCode, model, context.RelativePath);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[variance-use-site-normalize] " +
                $"{(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
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
    {
        var reportPath = Path.Combine(
            tree.IntermediateRoot, "variance-use-site-normalize-report.json");
        File.WriteAllText(
            reportPath,
            _preprocessor.BuildReportJson(),
            new UTF8Encoding(false));

        _diagnostics.Add(new VarianceUseSiteDiagnostic(
            VarianceUseSiteSeverity.Info,
            VarianceUseSiteDiagnosticCode.CS2J1404,
            $"variance-use-site-normalize report written: {reportPath} " +
            $"({Statistics.SitesTotal} site(s), {Statistics.SitesRewritten} rewritten, " +
            $"{Statistics.SitesPreserved} preserved, " +
            $"{Statistics.SitesDeclaredUnsupported} unsupported)."));

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new VarianceUseSiteDiagnostic(
            VarianceUseSiteSeverity.Warning,
            VarianceUseSiteDiagnosticCode.CS2J1404,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new VarianceUseSiteDiagnostic(
            VarianceUseSiteSeverity.Error,
            VarianceUseSiteDiagnosticCode.CS2J1404,
            $"dotnet build failed with exit code {exitCode} after " +
            "variance-use-site-normalize preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new VarianceUseSiteDiagnostic(
            VarianceUseSiteSeverity.Error,
            VarianceUseSiteDiagnosticCode.CS2J1404,
            $"dotnet test failed with exit code {exitCode} after " +
            "variance-use-site-normalize preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Severity == VarianceUseSiteSeverity.Error);

    public override string BuildChainSummaryLine() => Statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != VarianceUseSiteSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();
}
