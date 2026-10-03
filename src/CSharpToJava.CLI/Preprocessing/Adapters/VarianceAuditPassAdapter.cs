using System.Text;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.VarianceAudit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the variance-audit (C33) pass. Read-only audit: it runs the standard
/// spine (runner builds the whole-tree compilation because
/// <c>NeedsSemanticModel</c> is true), audits every file, copies all files verbatim
/// and writes <c>variance-audit-report.json</c>. It never verifies build/tests
/// (design §5.1) and never emits a <see cref="PassFileRewrite"/>.
/// </summary>
internal sealed class VarianceAuditPassAdapter : ProjectPassAdapter
{
    private readonly VarianceAuditOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly VarianceAuditPreprocessor _preprocessor;
    private readonly List<VarianceAuditDiagnostic> _diagnostics = new();

    public VarianceAuditPassAdapter(
        VarianceAuditOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _preprocessor = new VarianceAuditPreprocessor(options);
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.VarianceAudit);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;

    // The audit never rewrites and its output tree is byte-identical to the input,
    // so restore/build/test verification is meaningless (design §3.2).
    public override bool VerifyBuild => false;
    public override bool VerifyTests => false;

    public VarianceAuditStatistics Statistics => _preprocessor.Statistics;
    public IReadOnlyList<VarianceAuditDiagnostic> Diagnostics => _diagnostics;

    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);

    public override IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
    {
        // Same synthesized global usings as the java-keywords pass so the audit
        // sees exactly the compilation the rest of the chain sees.
        var globalUsings = new StringBuilder();
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.Layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(
                intermediateProjectFile, tree.IntermediateRoot);
            if (projectUsings != null)
            {
                globalUsings.AppendLine(projectUsings);
            }
        }

        if (globalUsings.Length == 0)
        {
            return Array.Empty<SyntaxTree>();
        }

        return new[]
        {
            CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: "__cs2j_global_usings.g.cs"),
        };
    }

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        _preprocessor.BindCompilation(compilation);
        _preprocessor.Analyze();
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var model = context.SemanticModel
            ?? throw new InvalidOperationException(
                "variance-audit requires a semantic model for every file.");
        var audit = _preprocessor.AuditFile(model);
        foreach (var diagnostic in audit.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        // Read-only: every file is copied verbatim by the runner.
        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        Statistics.ReportsWritten = 1;
        var reportPath = Path.Combine(tree.IntermediateRoot, "variance-audit-report.json");
        File.WriteAllText(reportPath, _preprocessor.BuildReportJson(), new UTF8Encoding(false));
        return Task.CompletedTask;
    }

    // ---- failure hooks (unreachable while verify stays off; codes reserved) ----

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new VarianceAuditDiagnostic(
            VarianceSeverity.Warning,
            VarianceCodes.RestoreFailed,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new VarianceAuditDiagnostic(
            VarianceSeverity.Error,
            VarianceCodes.BuildFailed,
            $"dotnet build failed with exit code {exitCode} after variance-audit preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new VarianceAuditDiagnostic(
            VarianceSeverity.Error,
            VarianceCodes.TestsFailed,
            $"dotnet test failed with exit code {exitCode} after variance-audit preprocessing."));

    // Batch 1 emits no Error diagnostics, so this is always false; strict keeps the
    // M2 gate wired for batches 2/3.
    public override bool HasErrors
        => _options.Strict && _diagnostics.Any(d => d.Severity == VarianceSeverity.Error);

    public override string BuildChainSummaryLine() => Statistics.SummaryLine();

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != VarianceSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();
}