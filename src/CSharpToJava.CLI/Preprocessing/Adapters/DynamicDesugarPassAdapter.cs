using System.Text;
using CSharpToJava.Core.DynamicDesugar;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C25 dynamic-desugar pass (design §3.2/§5.1/§5.2). M1 ships
/// the Audit behavior only: the standard <see cref="ProjectPassAdapter"/> spine
/// parses/compiles the whole tree, this adapter binds the Core
/// <see cref="DynamicDesugarPreprocessor"/>, emits the named DS0xx diagnostics
/// per file (every file stays a byte copy) and writes
/// <c>dynamic-audit-report.json</c> into the intermediate root. Selecting
/// Rewrite mode in M1 changes only the diagnostic grading (Core policy); no
/// source is rewritten. The audit never restores/builds/tests the
/// intermediate tree (the M2 Rewrite adapter will, like the C42 Stub modes).
/// </summary>
internal sealed class DynamicDesugarPassAdapter : ProjectPassAdapter
{
    private const string ReportFileName = "dynamic-audit-report.json";
    private const string RestoreFailedCode = DynamicDesugarDiagnosticCodes.RestoreFailed;
    private const string BuildFailedCode = DynamicDesugarDiagnosticCodes.BuildFailed;
    private const string TestsFailedCode = DynamicDesugarDiagnosticCodes.TestsFailed;

    private readonly DynamicDesugarOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;

    private readonly DynamicDesugarPreprocessor _preprocessor;
    private readonly DynamicAuditReportSerializer _serializer = new();
    private readonly DynamicDesugarStatistics _statistics = new();
    private readonly List<ChainDiagnostic> _diagnostics = new();

    private PassTreeContext? _tree;
    private bool _bound;

    public DynamicDesugarPassAdapter(
        DynamicDesugarOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        bool verifyBuild,
        bool verifyTests)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _preprocessor = new DynamicDesugarPreprocessor(options);

        // Accepted for factory/descriptor alignment (verify dual-on defaults).
        // M1 Audit never restores/builds/tests, so the flags take effect only
        // with the M2 Rewrite mode (C42 ModeUsesVerify precedent).
        _ = verifyBuild;
        _ = verifyTests;
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.DynamicDesugar);

    public override string LogPrefix => "[dynamic-desugar]";
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;

    // M1: both modes are zero-rewrite audits, so the intermediate tree is
    // byte-identical to its input; restoring/building/testing it adds nothing
    // (design §5.1/§5.3). M2 Rewrite will return the constructor-gated flags.
    public override bool VerifyBuild => false;
    public override bool VerifyTests => false;
    public override bool DeleteBuildOutputsAfterRestore => false;

    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);

    public override string CompilationName => "dynamic-desugar-preprocess";

    public DynamicDesugarStatistics Statistics => _statistics;

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _tree = tree;
        return Task.CompletedTask;
    }

    public override IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
    {
        // Same synthesized global usings as the java-keywords / variance-audit
        // passes so the audit sees exactly the compilation the chain sees
        // (design §4.3/§5.1).
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
        // Whole-tree inventory is built once (Core caches it); per-file
        // RewriteFile calls below read the same cache.
        _preprocessor.BindCompilation(compilation, _options);
        _preprocessor.BuildInventory();
        _bound = true;
        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        if (!_bound)
        {
            throw new InvalidOperationException(
                "dynamic-desugar requires the whole-tree compilation before file rewrites.");
        }

        // Drive the Core per-file (JK-style) overload with this file's model so
        // the DS0xx diagnostics land here, where the pipeline already knows the
        // source-root-relative path (design §5.1: RelativePath prefix reuse).
        // M1: the result is always Changed=false; the runner byte-copies the
        // file. Audit vs Rewrite differs only in the severities Core resolves.
        var sink = new List<DynamicDesugarDiagnostic>();
        _preprocessor.RewriteFile(
            context.SourceFilePath,
            context.SourceCode,
            context.SemanticModel,
            _statistics,
            sink);

        foreach (var diagnostic in sink)
        {
            _diagnostics.Add(new ChainDiagnostic(
                LevelOf(diagnostic.Severity),
                diagnostic.Code,
                diagnostic.Message,
                context.RelativePath,
                diagnostic.Line,
                diagnostic.Column));
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // Zero sites still writes the report (design §3.5).
        var report = RelativizeReport(_preprocessor.CloseReport());
        var reportPath = Path.Combine(tree.IntermediateRoot, ReportFileName);
        _serializer.WriteToFile(reportPath, report);

        _statistics.ReportsWritten = 1;
        var written = DynamicDesugarDiagnostic.CreateReportWritten(
            reportPath, report.Totals.Sites);
        _diagnostics.Add(new ChainDiagnostic(
            "info",
            written.Code,
            written.Message,
            reportPath));

        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ChainDiagnostic(
            "warning",
            RestoreFailedCode,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget));

    // Unreachable while M1 verification stays off; codes reserved for M2.
    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ChainDiagnostic(
            "error",
            BuildFailedCode,
            $"dotnet build failed with exit code {exitCode} after dynamic-desugar preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ChainDiagnostic(
            "error",
            TestsFailedCode,
            $"dotnet test failed with exit code {exitCode} after dynamic-desugar preprocessing."));

    public override bool HasErrors =>
        _preprocessor.HasErrors || _diagnostics.Any(d => d.Level == "error");

    public override string? BuildChainSummaryLine()
    {
        if (!_bound)
        {
            return null;
        }

        // design §5.1: N sites (P proven / U unprovable / S manifest-suppressed),
        // R concretized declarations, K residual; M1 P/R/S/K are structurally 0.
        return $"dynamic-desugar: {_statistics.SitesTotal} site(s) " +
               $"({_statistics.ProvenDeclarations} proven, " +
               $"{_statistics.UnprovableSites} unprovable, " +
               $"{_statistics.ManifestSuppressed} manifest-suppressed), " +
               $"{_statistics.DeclarationsRewritten} declaration(s) concretized, " +
               $"{_statistics.ResidualSites} residual; mode={_options.Mode}";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Level != "info" || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    /// <summary>
    /// Rebuild the report with every site file relativized to the source root
    /// (e.g. <c>src/Lib/Foo.cs</c>) so the JSON is byte-stable across runs and
    /// destination directories (design §3.5/§4.6 byte-stability contract).
    /// </summary>
    private DynamicAuditReport RelativizeReport(DynamicAuditReport report)
    {
        var sites = report.Sites
            .Select(entry => new DynamicAuditSiteEntry
            {
                File = ToSourceRelative(entry.File),
                Line = entry.Line,
                Column = entry.Column,
                Span = entry.Span,
                Kind = entry.Kind,
                Code = entry.Code,
                SyntaxKind = entry.SyntaxKind,
                BoundSymbol = entry.BoundSymbol,
                Receiver = entry.Receiver,
                Expression = entry.Expression,
                Provability = entry.Provability,
                Reason = entry.Reason,
                OriginType = entry.OriginType,
                ManifestHit = entry.ManifestHit,
                SymbolUnresolved = entry.SymbolUnresolved,
                Message = entry.Message,
            })
            .ToList();

        return new DynamicAuditReport
        {
            Tool = report.Tool,
            Mode = report.Mode,
            Totals = report.Totals,
            Sites = sites,
        };
    }

    private string ToSourceRelative(string path)
    {
        var root = _tree?.SourceRoot;
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
        {
            return path;
        }

        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? path
            : relative.Replace('\\', '/');
    }

    private static string LevelOf(DynamicDesugarSeverity severity) => severity switch
    {
        DynamicDesugarSeverity.Error => "error",
        DynamicDesugarSeverity.Warning => "warning",
        _ => "info",
    };

    private sealed record ChainDiagnostic(
        string Level,
        string Code,
        string Message,
        string? FilePath = null,
        int Line = 0,
        int Column = 0)
    {
        public string ToMsBuildString()
        {
            var location = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
            return $"{location}{Level} {Code}: {Message}";
        }
    }
}
