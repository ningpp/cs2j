using System.Text;
using System.Xml;
using System.Xml.Linq;
using CSharpToJava.Core.DynamicDesugar;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C25 dynamic-desugar pass (design §3.2/§5.1/§5.3). The
/// standard <see cref="ProjectPassAdapter"/> spine parses/compiles the whole
/// tree; this adapter binds the Core <see cref="DynamicDesugarPreprocessor"/>
/// and, in both modes, precomputes one whole-tree <c>RewriteAll()</c> result in
/// <see cref="CompilationReadyAsync"/> (C42 precompute-dispatch precedent):
/// <list type="bullet">
/// <item><b>Audit</b> (default): every file is a byte copy; the named DS0xx
/// diagnostics flow from the precomputed result and
/// <c>dynamic-audit-report.json</c> is written; no restore/build/test;</item>
/// <item><b>Rewrite</b>: Changed files are served from the precomputed result
/// (<see cref="PassFileRewrite.PreserveBom"/> on), the residual double-sweep
/// gate (DS030/DS060/DS061) closes the run in Core, and intermediate-tree
/// restore/build/test verification is dual-on (gated by the global
/// <c>--no-verify-*</c>).</item>
/// </list>
/// </summary>
internal sealed class DynamicDesugarPassAdapter : ProjectPassAdapter
{
    private const string ReportFileName = "dynamic-audit-report.json";
    private const string ManifestFileName = "Cs2jDynamicAudit.json";
    private const string RestoreFailedCode = DynamicDesugarDiagnosticCodes.RestoreFailed;
    private const string BuildFailedCode = DynamicDesugarDiagnosticCodes.BuildFailed;
    private const string TestsFailedCode = DynamicDesugarDiagnosticCodes.TestsFailed;

    private readonly DynamicDesugarOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly DynamicDesugarPreprocessor _preprocessor;
    private readonly DynamicAuditReportSerializer _serializer = new();
    private readonly List<ChainDiagnostic> _diagnostics = new();

    private PassTreeContext? _tree;
    private DynamicApplyResult? _applyResult;
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
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _preprocessor = new DynamicDesugarPreprocessor(options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.DynamicDesugar);

    public override string LogPrefix => "[dynamic-desugar]";
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;

    // Verify dual-on applies to Rewrite only (§5.1/§5.3); Audit copies bytes
    // and never restores/builds/tests the intermediate tree.
    public override bool VerifyBuild => ModeUsesVerify && _verifyBuild;
    public override bool VerifyTests => ModeUsesVerify && _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => ModeUsesVerify;

    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);

    public override string CompilationName => "dynamic-desugar-preprocess";

    public override IReadOnlyCollection<MetadataReference> GetMetadataReferences()
    {
        var references = new List<MetadataReference>(base.GetMetadataReferences());
        if (_tree is null)
        {
            return references;
        }

        // Symbol-first for explicit assembly references (design §4.1/R11): a
        // <Reference HintPath="..."/> assembly participates in the intermediate
        // build but is not part of the TPA baseline set. Without it, an
        // external object+[Dynamic] member (DS019) is invisible to the semantic
        // scan, and no textual marker can conservatively replace it (the call
        // text looks like an ordinary resolved call). Trees without HintPath
        // references keep byte-identical reference sets.
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in _tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                _tree.IntermediateRoot, Path.GetRelativePath(_tree.Layout.SourceRoot, projectFile));

            // Stage order: the compilation is built before the spine copies
            // non-project assets, so HintPath files may only exist next to the
            // original project at this point; each candidate is resolved in the
            // intermediate tree first and falls back to the source tree.
            var intermediateProjectDir = Path.GetFullPath(Path.GetDirectoryName(intermediateProjectFile)!);
            var sourceProjectDir = Path.GetFullPath(Path.GetDirectoryName(projectFile)!);
            AddHintPathReferences(
                intermediateProjectFile, sourceProjectDir, intermediateProjectDir, references, added);
        }

        return references;
    }

    private static void AddHintPathReferences(
        string projectFile,
        string sourceProjectDir,
        string intermediateProjectDir,
        List<MetadataReference> references,
        HashSet<string> added)
    {
        try
        {
            if (!File.Exists(projectFile))
            {
                return;
            }

            var doc = XDocument.Load(projectFile);
            if (doc.Root is null)
            {
                return;
            }

            foreach (var reference in doc.Root.Descendants("Reference"))
            {
                var hintPath = reference.Elements("HintPath")
                    .Select(element => element.Value?.Trim())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var candidate = !string.IsNullOrWhiteSpace(hintPath)
                    ? hintPath
                    : reference.Attribute("Include")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(candidate)
                    || candidate.Contains(';')
                    || candidate.Contains("$(", StringComparison.Ordinal)
                    || candidate.Contains('%')
                    || !candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var intermediatePath = Path.GetFullPath(Path.Combine(intermediateProjectDir, candidate));
                var sourcePath = Path.GetFullPath(Path.Combine(sourceProjectDir, candidate));
                var fullPath = File.Exists(intermediatePath)
                    ? intermediatePath
                    : File.Exists(sourcePath) ? sourcePath : null;
                if (fullPath is not null && added.Add(fullPath))
                {
                    references.Add(MetadataReference.CreateFromFile(fullPath));
                }
            }
        }
        catch (IOException)
        {
            // Unreadable project file: fall back to baseline references only.
        }
        catch (XmlException)
        {
            // Non-XML/legacy project file: fall back to baseline references only.
        }
    }

    /// <summary>预计算统计（闸门/具体化取数；未绑定时为空统计）。</summary>
    public DynamicDesugarStatistics Statistics =>
        _applyResult?.Statistics ?? new DynamicDesugarStatistics();

    private bool ModeUsesVerify => _options.Mode == DynamicMode.Rewrite;

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
        // 两种模式统一整树预计算（C42 范本）：RewriteAll 内 Audit 只发分级诊断
        // 字节复制，Rewrite 额外做 A1 具体化与 residual 双扫闸门。
        _preprocessor.BindCompilation(compilation, ResolveOptions());
        _applyResult = _preprocessor.RewriteAll();
        _bound = true;
        // TEMPORARY profiling dump (dynamic-desugar perf task). Remove before finishing.
        DynamicDesugarProfiling.Dump();

        foreach (var diagnostic in _applyResult.Diagnostics)
        {
            _diagnostics.Add(new ChainDiagnostic(
                LevelOf(diagnostic.Severity),
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.FilePath is null ? null : ToSourceRelative(diagnostic.FilePath),
                diagnostic.Line,
                diagnostic.Column));
        }

        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        if (!_bound || _applyResult == null)
        {
            throw new InvalidOperationException(
                "dynamic-desugar requires the whole-tree compilation before file rewrites.");
        }

        // Audit: Core marks every file Changed=false → null (byte copy).
        // Rewrite: only concretized files are served; gate failures do not
        // un-write files here, but fail the run via HasErrors (no delivered
        // partial-isolation tree, design §5.3).
        var file = _applyResult.Files.FirstOrDefault(f =>
            string.Equals(f.FilePath, context.SourceFilePath, StringComparison.OrdinalIgnoreCase));
        if (file is { Changed: true })
        {
            return Task.FromResult<PassFileRewrite?>(new PassFileRewrite
            {
                OutputCode = file.OutputCode,
                Changed = true,
                PreserveBom = true,
            });
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // Zero sites still writes the report (design §3.5); both modes write it.
        var report = RelativizeReport(_preprocessor.CloseReport());
        var reportPath = Path.Combine(tree.IntermediateRoot, ReportFileName);
        _serializer.WriteToFile(reportPath, report);

        if (_applyResult != null)
        {
            _applyResult.Statistics.ReportsWritten = 1;
        }

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
        if (!_bound || _applyResult == null)
        {
            return null;
        }

        // design §5.1: N sites (P proven / U unprovable / S manifest-suppressed),
        // R declaration(s) concretized, K residual.
        var statistics = _applyResult.Statistics;
        return $"dynamic-desugar: {statistics.SitesTotal} site(s) "
               + $"({statistics.ProvenDeclarations} proven, "
               + $"{statistics.UnprovableSites} unprovable, "
               + $"{statistics.ManifestSuppressed} manifest-suppressed), "
               + $"{statistics.DeclarationsRewritten} declaration(s) concretized, "
               + $"{statistics.ResidualSites} residual; mode={_options.Mode}";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Level != "info" || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    /// <summary>
    /// 解析实际生效选项（开放问题 2 决议：manifest 随源码受控、落源根，不走
    /// CWD/config 约定）：显式 <c>--dynamic-audit-manifest</c> 相对路径按调用方
    /// CWD 解析（Core 只按给定路径加载）；未指定时若源根存在
    /// <c>Cs2jDynamicAudit.json</c> 则默认采用，缺省=无 manifest。
    /// </summary>
    private DynamicDesugarOptions ResolveOptions()
    {
        if (!string.IsNullOrWhiteSpace(_options.AuditManifestPath))
        {
            var explicitPath = Path.IsPathRooted(_options.AuditManifestPath)
                ? _options.AuditManifestPath
                : Path.GetFullPath(_options.AuditManifestPath);
            return WithManifestPath(_options, explicitPath);
        }

        if (_tree != null)
        {
            var sourceRootManifest = Path.Combine(_tree.SourceRoot, ManifestFileName);
            if (File.Exists(sourceRootManifest))
            {
                return WithManifestPath(_options, sourceRootManifest);
            }
        }

        return _options;
    }

    private static DynamicDesugarOptions WithManifestPath(
        DynamicDesugarOptions options,
        string manifestPath)
        => new()
        {
            Mode = options.Mode,
            Strict = options.Strict,
            AuditManifestPath = manifestPath,
            SealedOnly = options.SealedOnly,
            AllowCastInsertion = options.AllowCastInsertion,
        };

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
