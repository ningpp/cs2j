using System.Text;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.PlatformIsolation;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;
using CoreDiagnosticSeverity = CSharpToJava.Core.Context.DiagnosticSeverity;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C42 platform-isolation pass, M1 Report mode (design §5).
/// The standard <see cref="ProjectPassAdapter"/> spine parses/compiles the whole
/// tree; this adapter binds the Core <see cref="PlatformIsolationPreprocessor"/>,
/// builds the whole-tree boundary inventory once, and writes
/// <c>platform-isolation-report.json</c> into the intermediate tree root. Source
/// files are copied verbatim (<see cref="RewriteFileAsync"/> is always null), no
/// restore/build/test verification runs (<see cref="VerifyBuild"/>/<see cref="VerifyTests"/>
/// are false), and <c>DeleteBuildOutputsAfterRestore</c> is false.
///
/// Non-Report modes (Reject/Stub/Facade) are M2/M3; enabling one in M1 fails
/// loudly instead of silently degrading to Report.
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

    private readonly PlatformIsolationPreprocessor _preprocessor = new();
    private readonly PlatformIsolationReportSerializer _serializer = new();
    private readonly List<ChainDiagnostic> _diagnostics = new();

    private PassTreeContext? _tree;
    private BoundaryInventory? _inventory;
    private PlatformIsolationReportBuildResult? _report;
    private PlatformIsolationReport? _outputReport;
    private bool _modeUnsupported;

    public PlatformIsolationPassAdapter(
        PlatformIsolationOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.PlatformIsolation);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;

    // M1 Report mode: no intermediate-tree restore/build/test verification and no
    // bin cleanup. The descriptor dual-on default applies to M2 Stub/Facade.
    public override bool VerifyBuild => false;
    public override bool VerifyTests => false;
    public override bool DeleteBuildOutputsAfterRestore => false;

    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest, preprocessorSymbols: new[] { "NETCOREAPP" });

    public override string CompilationName => "platform-isolation-preprocess";

    public PlatformIsolationStatistics? Statistics => _report?.Statistics;
    public IReadOnlyList<BoundarySite> Sites => _inventory?.Sites ?? (IReadOnlyList<BoundarySite>)Array.Empty<BoundarySite>();

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

        if (_options.Mode != PlatformIsolationMode.Report)
        {
            // 禁止静默降级为 Report：M1 只交付 Report 模式。
            _modeUnsupported = true;
            _diagnostics.Add(new ChainDiagnostic(
                "error",
                PreprocessDiagnosticCodes.StubNotImplemented,
                $"platform-isolation mode '{_options.Mode}' is not implemented until C42 M2/M3; " +
                "only Report mode is available in M1."));
            return Task.CompletedTask;
        }

        _inventory = _preprocessor.BuildInventory();
        _report = _preprocessor.BuildReport();
        _outputReport = RelativizeReport(_report.Report);

        EmitSiteDiagnostics();
        EmitCoreDiagnostics(_report.Diagnostics);

        return Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Report mode is a byte-for-byte copy: no source is ever rewritten.
        _ = context;
        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override async Task AfterFilesAsync(PassTreeContext tree)
    {
        if (_modeUnsupported || _report is null || _outputReport is null)
        {
            return;
        }

        var reportPath = Path.Combine(tree.IntermediateRoot, ReportFileName);
        await File.WriteAllTextAsync(
            reportPath,
            _serializer.Serialize(_outputReport),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var statistics = _report.Statistics;
        _diagnostics.Add(new ChainDiagnostic(
            "info",
            PlatformIsolationDiagnosticCodes.ReportWritten,
            $"platform-isolation report written: {reportPath}; {statistics.TotalSites} site(s); " +
            $"reject={statistics.Rejected}, stub={statistics.Stubbed}, facade={statistics.Facaded}, " +
            $"suppressed={statistics.Suppressed}, residual={statistics.Residual}; " +
            $"reconciled={statistics.InvariantHolds}",
            reportPath));
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

    public override bool HasErrors => _diagnostics.Any(d => d.Level == "error");

    public override string? BuildChainSummaryLine()
    {
        if (_modeUnsupported)
        {
            return $"platform-isolation: mode '{_options.Mode}' not implemented (M2/M3); no report written";
        }

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
            .Where(d => d.Level != "info" || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

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