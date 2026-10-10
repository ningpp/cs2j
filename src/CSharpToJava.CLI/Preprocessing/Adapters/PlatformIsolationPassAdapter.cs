using System.Text;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.PlatformIsolation;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;
using CoreDiagnosticSeverity = CSharpToJava.Core.Context.DiagnosticSeverity;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C42 platform-isolation pass (design §3.5/§5). The standard
/// <see cref="ProjectPassAdapter"/> spine parses/compiles the whole tree; this
/// adapter binds the Core <see cref="PlatformIsolationPreprocessor"/> and drives
/// one of four modes:
/// <list type="bullet">
/// <item><b>Report</b> (default): byte-copy + <c>platform-isolation-report.json</c>, no verify;</item>
/// <item><b>Reject</b>: no source change; every Reject site emits PI030 Error
/// (original CS2J3xxx code + identity + suggestion) → the chain fails, exit != 0;</item>
/// <item><b>Stub</b>: whole-tree apply via Core (probe redirect / PNSE stubs / guard strip),
/// generated files distributed under <c>generated/</c>, intermediate-tree build/test
/// verification dual-on (gated by the global <c>--no-verify-*</c>);</item>
/// <item><b>Facade</b> (M3): two-phase whole-tree apply — Stub channel first, then
/// <c>FacadeGenerator</c> emits port interfaces + partial seams + user slots
/// (no port implementation; PI020 Info), same verification dual-on as Stub.</item>
/// </list>
/// "拒绝优先、不交付部分改写"(§3.5): any PI040/PI031 Error makes the whole run fail
/// (<see cref="HasErrors"/>), so no partial-isolation tree is delivered.
/// </summary>
internal sealed class PlatformIsolationPassAdapter : ProjectPassAdapter
{
    private const string ReportFileName = "platform-isolation-report.json";
    private const string RestoreFailedCode = "PI050";
    private const string BuildFailedCode = "PI051";
    private const string TestsFailedCode = "PI052";

    private readonly PlatformIsolationOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly PlatformIsolationPreprocessor _preprocessor = new();
    private readonly PlatformIsolationReportSerializer _serializer = new();
    private readonly List<ChainDiagnostic> _diagnostics = new();

    private PassTreeContext? _tree;
    private BoundaryInventory? _inventory;
    private PlatformIsolationReportBuildResult? _report;
    private PlatformIsolationReport? _outputReport;
    private PlatformIsolationApplyResult? _applyResult;

    public PlatformIsolationPassAdapter(
        PlatformIsolationOptions options,
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
        PassCatalog.ImplementedById(PassStubCatalog.Ids.PlatformIsolation);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;

    // Verification dual-on applies only to the Stub/Facade apply modes (§3.1/§5);
    // Report/Reject never restore-build the intermediate tree.
    public override bool VerifyBuild => ModeUsesVerify && _verifyBuild;
    public override bool VerifyTests => ModeUsesVerify && _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => ModeUsesVerify;

    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest, preprocessorSymbols: new[] { "NETCOREAPP" });

    public override string CompilationName => "platform-isolation-preprocess";

    public PlatformIsolationStatistics? Statistics => _report?.Statistics;
    public IReadOnlyList<BoundarySite> Sites =>
        _inventory?.Sites ?? (IReadOnlyList<BoundarySite>)Array.Empty<BoundarySite>();

    private bool ModeUsesVerify =>
        _options.Mode is PlatformIsolationMode.Stub or PlatformIsolationMode.Facade;

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _tree = tree;
        return Task.CompletedTask;
    }

    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        var isTestProject = _tree?.ProjectGraph?.ProjectsInTopologicalOrder
            .FirstOrDefault()?.Kind == ProjectKind.Test;
        _preprocessor.BindCompilation(
            compilation, _options, isTestProject, _compatNamespacePrefixes);

        _inventory = _preprocessor.BuildInventory();
        _report = _preprocessor.BuildReport();
        _outputReport = RelativizeReport(_report.Report);

        if (_options.Mode == PlatformIsolationMode.Report)
        {
            EmitSiteDiagnostics();
            EmitCoreDiagnostics(_report.Diagnostics);
        }
        else
        {
            // Reject / Stub / Facade：整树一次 apply（Facade 在 Core 内走两遍组合），
            // 动作码 + residual 闸门由 Core 产出。
            _applyResult = _preprocessor.RewriteAll();
            foreach (var diagnostic in _applyResult.Diagnostics)
            {
                _diagnostics.Add(new ChainDiagnostic(
                    LevelOf(diagnostic.Severity),
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.FilePath is null ? null : ToSourceRelative(diagnostic.FilePath)));
            }
        }

        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Report/Reject: byte-copy. Stub/Facade: serve the precomputed Core rewrite.
        if (_applyResult == null)
        {
            return Task.FromResult<PassFileRewrite?>(null);
        }

        var file = _applyResult.Files.FirstOrDefault(f =>
            string.Equals(f.FilePath, context.SourceFilePath, StringComparison.OrdinalIgnoreCase));
        if (file is { Changed: true })
        {
            return Task.FromResult<PassFileRewrite?>(new PassFileRewrite
            {
                OutputCode = file.OutputCode,
                Changed = true,
            });
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_outputReport != null)
        {
            var reportPath = Path.Combine(tree.IntermediateRoot, ReportFileName);
            await File.WriteAllTextAsync(
                reportPath,
                _serializer.Serialize(_outputReport),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var statistics = _report!.Statistics;
            _diagnostics.Add(new ChainDiagnostic(
                "info",
                PlatformIsolationDiagnosticCodes.ReportWritten,
                $"platform-isolation report written: {reportPath}; {statistics.TotalSites} site(s); " +
                $"reject={statistics.Rejected}, stub={statistics.Stubbed}, facade={statistics.Facaded}, " +
                $"suppressed={statistics.Suppressed}, residual={statistics.Residual}; " +
                $"reconciled={statistics.InvariantHolds}",
                reportPath));
        }

        if (_applyResult != null && _applyResult.GeneratedFiles.Count > 0)
        {
            // 名字碰撞保护（design §7.2）：手写源已声明 Cs2jPlatformProbe → fail-fast，
            // 不覆盖、不产出（沿用适配器既有 fail-fast 口径）。
            var collision = FindProbeNameCollision(tree);
            if (collision != null)
            {
                _diagnostics.Add(new ChainDiagnostic(
                    "error",
                    PreprocessDiagnosticCodes.StubNotImplemented,
                    $"Generated probe shim collides with an existing declaration of " +
                    $"'{StubGenerator.ProbeClassName}' in '{collision}'; refusing to overwrite."));
                return;
            }

            await DistributeGeneratedFilesAsync(tree, _applyResult.GeneratedFiles);
        }
    }

    private static string? FindProbeNameCollision(PassTreeContext tree)
    {
        foreach (var sourceFile in tree.CsFiles)
        {
            if (!File.Exists(sourceFile))
            {
                continue;
            }

            var text = File.ReadAllText(sourceFile);
            if (text.Contains("class " + StubGenerator.ProbeClassName, StringComparison.Ordinal)
                || text.Contains("struct " + StubGenerator.ProbeClassName, StringComparison.Ordinal))
            {
                return Path.GetFileName(sourceFile);
            }
        }

        return null;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new ChainDiagnostic(
            "warning", RestoreFailedCode,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new ChainDiagnostic(
            "error", BuildFailedCode,
            $"dotnet build failed with exit code {exitCode} after platform-isolation preprocessing."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new ChainDiagnostic(
            "error", TestsFailedCode,
            $"dotnet test failed with exit code {exitCode} after platform-isolation preprocessing."));

    public override bool HasErrors =>
        _diagnostics.Any(d => d.Level == "error");

    public override string? BuildChainSummaryLine()
    {
        if (_report is null)
        {
            return null;
        }

        var statistics = _report.Statistics;
        var ui = FamilyCount(statistics, BoundaryFamily.UiNamespace);
        var platform = FamilyCount(statistics, BoundaryFamily.PlatformType)
                       + FamilyCount(statistics, BoundaryFamily.OsGuardAttribute);
        var probes = FamilyCount(statistics, BoundaryFamily.PlatformMember);
        var interop = FamilyCount(statistics, BoundaryFamily.NativeAttribute)
                      + FamilyCount(statistics, BoundaryFamily.NativeType)
                      + FamilyCount(statistics, BoundaryFamily.ExternMethod);

        return $"platform-isolation: {statistics.TotalSites} site(s) " +
               $"(UI {ui}, platform {platform}, probes {probes}, interop {interop}); " +
               $"reject={statistics.Rejected}, stub={statistics.Stubbed}, " +
               $"facade={statistics.Facaded}, suppressed={statistics.Suppressed}, " +
               $"residual={statistics.Residual}; mode={_options.Mode}";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            // Per-site apply records (PI010/011/012/020) are the rewrite audit
            // trail and show by default, mirroring PI001 in Report mode; only the
            // PI070 report-path detail stays verbose-only.
            .Where(d => d.Level != "info"
                        || verbose
                        || d.Code != PlatformIsolationDiagnosticCodes.ReportWritten)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private static async Task DistributeGeneratedFilesAsync(
        PassTreeContext tree,
        IReadOnlyList<GeneratedFile> generatedFiles)
    {
        var projectDirectories = EnumerateProjectDirectories(tree.IntermediateRoot).ToList();

        foreach (var generated in generatedFiles)
        {
            var relativePath = generated.RelativePath.Replace('/', Path.DirectorySeparatorChar);

            // Tree-root copy stays for audit (§3.4).
            var destination = Path.Combine(tree.IntermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(
                destination, generated.Content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Every generated artifact must also live INSIDE each project directory,
            // otherwise the project's default Compile glob never sees it: the probe
            // shim backs redirected call sites that may live in any project, and M3
            // facade ports/slots (generated/&lt;ns&gt;/...) are distributed preserving
            // their relative subpath.
            foreach (var projectDirectory in projectDirectories)
            {
                var projectCopy = Path.Combine(projectDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(projectCopy)!);
                await File.WriteAllTextAsync(
                    projectCopy, generated.Content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
    }

    private static IEnumerable<string> EnumerateProjectDirectories(string intermediateRoot)
        => Directory
            .EnumerateFiles(intermediateRoot, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(directory => directory != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)!;

    private void EmitSiteDiagnostics()
    {
        if (_outputReport is null || _inventory is null)
        {
            return;
        }

        // Report.Sites preserves inventory order (both sorted), so index i maps.
        for (var i = 0; i < _outputReport.Sites.Count; i++)
        {
            var entry = _outputReport.Sites[i];
            var severity = _options.Strict
                           && _inventory.Sites[i].Severity == CoreDiagnosticSeverity.Error
                ? "error"
                : "warning";

            _diagnostics.Add(new ChainDiagnostic(
                severity,
                PlatformIsolationDiagnosticCodes.SiteRecorded,
                $"{entry.Family} site '{entry.Identity}' at {entry.File}({entry.Line + 1},{entry.Column}): " +
                $"{entry.Action}; recorded by platform-isolation report mode",
                entry.File,
                entry.Line + 1,
                entry.Column));
        }
    }

    private void EmitCoreDiagnostics(IReadOnlyList<PlatformIsolationDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            // PI001 is emitted per site above with the report-mode severity;
            // PI070 is emitted in AfterFilesAsync once the path is known.
            if (diagnostic.Code is PlatformIsolationDiagnosticCodes.SiteRecorded
                or PlatformIsolationDiagnosticCodes.ReportWritten)
            {
                continue;
            }

            _diagnostics.Add(new ChainDiagnostic(
                LevelOf(diagnostic.Severity),
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.FilePath is null ? null : ToSourceRelative(diagnostic.FilePath)));
        }
    }

    /// <summary>
    /// Rebuild the report with file paths relative to the source root
    /// (source-structure-relative, e.g. <c>src/Env/OsInfo.cs</c>) so the report is
    /// byte-stable across runs and destination directories (design §4.6).
    /// </summary>
    private PlatformIsolationReport RelativizeReport(PlatformIsolationReport report)
    {
        var sites = report.Sites
            .Select(entry => new PlatformIsolationSiteEntry
            {
                File = ToSourceRelative(entry.File),
                Line = entry.Line,
                Column = entry.Column,
                Span = entry.Span,
                Family = entry.Family,
                Code = entry.Code,
                Identity = entry.Identity,
                SyntaxKind = entry.SyntaxKind,
                DefaultAction = entry.DefaultAction,
                Action = entry.Action,
                Replacement = entry.Replacement,
                Diagnostic = entry.Diagnostic,
                ConfigMatch = entry.ConfigMatch,
                Confidence = entry.Confidence,
            })
            .ToList();

        return new PlatformIsolationReport
        {
            Tool = report.Tool,
            Mode = report.Mode,
            GeneratedAtUtc = report.GeneratedAtUtc,
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

    private static string LevelOf(CoreDiagnosticSeverity severity) => severity switch
    {
        CoreDiagnosticSeverity.Error => "error",
        CoreDiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    private static int FamilyCount(PlatformIsolationStatistics statistics, BoundaryFamily family)
        => statistics.ByFamily.GetValueOrDefault(family);

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