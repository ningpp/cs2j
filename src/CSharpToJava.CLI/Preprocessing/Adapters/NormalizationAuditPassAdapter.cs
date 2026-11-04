using System.Text;
using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// M0-b: read-only normalization-audit pass. The standard spine builds the
/// whole-tree compilation because <c>NeedsSemanticModel</c> is true; every file
/// is scanned by <see cref="ResidualRegistry"/> and copied verbatim (null
/// rewrite). After the tree is processed the residual report is written to
/// <c>&lt;destination&gt;/preprocess-residual-report.json</c> (or an explicit
/// <c>--residual-report</c> path), and a residual makes the run fail when the
/// profile enforces readiness (java-ready / default) instead of observing
/// (audit).
/// </summary>
internal sealed class NormalizationAuditPassAdapter : ProjectPassAdapter
{
    private const int NonVerboseDiagnosticLimit = 20;

    private readonly IReadOnlyList<string> _chain;
    private readonly string _profile;
    private readonly bool _failOnResidual;
    private readonly string? _reportPath;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    /// <summary>
    /// Top-level directories of the pass-injected support libraries. These are
    /// symbol sources only: the Java converter filters them out, so their
    /// residual shapes are not user-code normalization debt.
    /// </summary>
    private static readonly string[] SupportLibraryTopLevelDirectories =
    {
        "Cs2jRefOutIn",
        "Cs2jPrelude",
        "Cs2jUnsignedMath",
        "Cs2jArray",
        "Cs2jTuples",
    };

    private readonly List<ResidualHit> _hits = new();
    private int _skippedSupportLibraryFiles;

    public NormalizationAuditPassAdapter(
        IReadOnlyList<string> chain,
        string profile,
        bool failOnResidual,
        string? reportPath,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _chain = chain;
        _profile = profile;
        _failOnResidual = failOnResidual;
        _reportPath = reportPath;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.NormalizationAudit);
    public override string LogPrefix => "[normalization-audit]";
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;

    // The pass never rewrites and its output tree is byte-identical to the
    // input, so restore/build/test verification would be meaningless.
    public override bool VerifyBuild => false;
    public override bool VerifyTests => false;
    public override bool DeleteBuildOutputsAfterRestore => false;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Injected support libraries never reach the Java converter, so their
        // ref structs/stackalloc shapes are not user-code residual debt.
        if (IsSupportLibraryRelativePath(context.RelativePath))
        {
            _skippedSupportLibraryFiles++;
            return Task.FromResult<PassFileRewrite?>(null);
        }

        var model = context.SemanticModel
            ?? throw new InvalidOperationException(
                "normalization-audit requires a semantic model for every file.");

        _hits.AddRange(ResidualRegistry.CreateDefault().Scan(model, context.RelativePath));

        // Read-only: the runner copies every file byte-for-byte.
        return Task.FromResult<PassFileRewrite?>(null);
    }

    /// <summary>
    /// True when the file's top-level tree directory is an injected support
    /// library project. Path separators and casing are normalized.
    /// </summary>
    internal static bool IsSupportLibraryRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var separator = normalized.IndexOf('/');
        var topLevel = separator >= 0 ? normalized[..separator] : normalized;

        return SupportLibraryTopLevelDirectories.Any(directory =>
            string.Equals(directory, topLevel, StringComparison.OrdinalIgnoreCase));
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        var failHits = _hits.Count(hit => hit.Policy == ResidualPolicy.Fail);
        var report = new ResidualReport(
            SchemaVersion: 1,
            Profile: _profile,
            Chain: _chain,
            Summary: new ResidualReportSummary(
                FilesScanned: tree.CsFiles.Count,
                Residuals: _hits.Count,
                Errors: _failOnResidual ? failHits : 0,
                Warnings: _failOnResidual ? 0 : _hits.Count),
            Residuals: _hits.Select(hit => new ResidualReportEntry(
                Code: hit.Code,
                File: hit.FilePath,
                Line: hit.Line,
                Column: hit.Column,
                Snippet: hit.Snippet,
                OwnerPassId: hit.OwnerPassId,
                Policy: hit.Policy.ToString(),
                Reason: hit.Reason)).ToList());

        var path = ResolveReportPath(tree.DestinationRoot);
        var directory = Path.GetDirectoryName(path);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        ResidualReportWriter.Write(path, report);
        return Task.CompletedTask;
    }

    // verify is always off, so these hooks are unreachable by design.
    public override void OnRestoreFailed(int exitCode, string restoreTarget)
    {
    }

    public override void OnBuildFailed(int exitCode)
    {
    }

    public override void OnTestsFailed(int exitCode)
    {
    }

    public override bool HasErrors =>
        _failOnResidual && _hits.Any(hit => hit.Policy == ResidualPolicy.Fail);

    public override string BuildChainSummaryLine()
    {
        var distinctFiles = _hits
            .Select(hit => hit.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var breakdown = string.Join(", ", _hits
            .GroupBy(hit => hit.Code)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}"));
        var skipped = _skippedSupportLibraryFiles > 0
            ? $", {_skippedSupportLibraryFiles} support-library file(s) skipped"
            : string.Empty;
        return $"normalization-audit: {_hits.Count} residual(s) across {distinctFiles} file(s) ({breakdown}){skipped}";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
    {
        var severity = _failOnResidual ? "[Error]" : "[Warning]";
        var lines = _hits
            .Select(hit => $"{severity} {hit.FilePath}({hit.Line},{hit.Column}): {hit.Code} {hit.Reason}")
            .ToList();

        if (!verbose && lines.Count > NonVerboseDiagnosticLimit)
        {
            var remaining = lines.Count - NonVerboseDiagnosticLimit;
            lines = lines.Take(NonVerboseDiagnosticLimit).ToList();
            lines.Add($"... ({remaining} more)");
        }

        return lines;
    }

    private string ResolveReportPath(string destinationRoot)
    {
        if (_reportPath is null)
        {
            return Path.Combine(destinationRoot, ResidualReportWriter.DefaultFileName);
        }

        return Path.IsPathRooted(_reportPath)
            ? Path.GetFullPath(_reportPath)
            : Path.GetFullPath(Path.Combine(destinationRoot, _reportPath));
    }
}
