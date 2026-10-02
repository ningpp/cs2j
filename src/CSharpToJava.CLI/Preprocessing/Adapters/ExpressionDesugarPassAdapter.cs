using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the expression-desugar family pass. M1 drives the C36
/// collection-expression pipeline: analyze (per-site plans/diagnostics; strict
/// errors atomically preserve the file), bottom-up rewrite, family
/// TempLifter statement insertion, CE-12 residual gate and formatting.
///
/// M3 adds the CE-GUARD semantic inventory (0585/0586/0587 via
/// <see cref="NewSyntaxGuardAnalyzer"/>; 0588 defended per site), the
/// verbose new-syntax inventory section (CS2J0584) and the AfterFiles
/// whole-tree residual reconciliation gate (CS2J0589): every
/// collection/spread node present in the intermediate tree on disk must be
/// paired with a skip diagnostic (0571/0576/0580/0581/0582/0583/0588),
/// including files copied verbatim. No support library, no AfterFiles
/// project references.
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

    /// <summary>
    /// Per-file statistics keyed by tree-relative path; the whole-tree gate
    /// reconciles each file's on-disk residuals against its own pairing.
    /// </summary>
    private readonly Dictionary<string, ExpressionDesugarStatistics> _fileStatistics =
        new(StringComparer.OrdinalIgnoreCase);

    private CollectionExprTreeGateResult? _gateResult;

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
        _fileStatistics[context.RelativePath] = result.Statistics;

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

    /// <summary>
    /// M3 CE-12 whole-tree gate: re-parse every intermediate .cs AS WRITTEN,
    /// count residual collection/spread nodes, and reconcile against the
    /// per-file paired skip accounting. Mismatch -> CS2J0589 Error makes the
    /// chain fail (covers verbatim-copied dirty files too); a balanced run
    /// emits CS2J0584 Info (verbose display only).
    /// </summary>
    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        var residualByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pairedByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        // Strict-Error files were atomically byte-preserved by the family
        // before the rewriter ran; the run already fails on their Error
        // diagnostic, so they take no part in the residual reconciliation.
        var atomicErrorFiles = new HashSet<string>(
            _diagnostics
                .Where(d => d.Severity == ExpressionDesugarSeverity.Error
                            && !string.IsNullOrEmpty(d.FilePath))
                .Select(d => d.FilePath!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var sourceFile in tree.CsFiles)
        {
            var relativePath = Path.GetRelativePath(tree.SourceRoot, sourceFile);
            if (HasCs2jOwnedSegment(relativePath)
                || atomicErrorFiles.Contains(relativePath))
            {
                continue;
            }

            var intermediateFile = Path.Combine(tree.IntermediateRoot, relativePath);
            if (!File.Exists(intermediateFile))
                continue;

            if (_fileStatistics.TryGetValue(relativePath, out var fileStats))
            {
                pairedByFile[relativePath] =
                    CollectionExprTreeGate.CountPairedNodes(fileStats.CollectionExpr);
            }
            else
            {
                pairedByFile[relativePath] = 0;
            }

            var source = File.ReadAllText(intermediateFile);
            var root = CSharpSyntaxTree.ParseText(source, parseOptions).GetCompilationUnitRoot();
            var residual = root.DescendantNodes()
                .Count(CollectionExprSyntaxFacts.IsResidualNode);
            if (residual > 0)
                residualByFile[relativePath] = residual;
        }

        _gateResult = CollectionExprTreeGate.Reconcile(residualByFile, pairedByFile);

        if (!_gateResult.Balanced)
        {
            var unpaired = string.Join(
                ", ",
                _gateResult.UnpairedFiles.Select(f =>
                    $"{f.File} ({f.ResidualNodes} residual/{f.PairedNodes} paired)"));
            _diagnostics.Add(new ExpressionDesugarDiagnostic(
                ExpressionDesugarSeverity.Error,
                CollectionExprDiagnosticCodes.CS2J0589,
                string.Format(
                    CollectionExprDiagnostics.TreeResidualMessage,
                    _gateResult.ResidualNodes,
                    _gateResult.PairedNodes,
                    unpaired.Length == 0 ? "?" : unpaired)));
        }
        else if (_verbose)
        {
            var c = _statistics.CollectionExpr;
            _diagnostics.Add(new ExpressionDesugarDiagnostic(
                ExpressionDesugarSeverity.Info,
                CollectionExprDiagnosticCodes.CS2J0584,
                string.Format(
                    CollectionExprDiagnostics.InventorySummaryMessage,
                    c.GuardStaticAbstractDeclarations,
                    c.GuardStaticAbstractCallSites,
                    c.GuardGenericAttributeDeclarations,
                    c.GuardGenericAttributeApplications,
                    c.GuardScopedParameterSites,
                    c.GuardScopedLocalSites,
                    _gateResult.ResidualNodes,
                    _gateResult.PairedNodes)));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Cs2j*-prefixed trees are this project's own emitted/owned sources
    /// (mirrors the project-test enumeration); they never participate in the
    /// residual gate.
    /// </summary>
    private static bool HasCs2jOwnedSegment(string relativePath)
        => relativePath
            .Replace('\\', '/')
            .Split('/')
            .Any(part => part.StartsWith("Cs2j", StringComparison.Ordinal));

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
               $"residual {c.ResidualCollectionCount}; " +
               $"guard: static-abstract {c.GuardStaticAbstractDeclarations} decl/" +
               $"{c.GuardStaticAbstractCallSites} call, generic-attr " +
               $"{c.GuardGenericAttributeDeclarations} decl/" +
               $"{c.GuardGenericAttributeApplications} app, scoped " +
               $"{c.GuardScopedParameterSites} param/{c.GuardScopedLocalSites} local";
    }

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
    {
        var lines = _diagnostics
            .Where(d => verbose || d.Severity != ExpressionDesugarSeverity.Info)
            .Select(d =>
            {
                var prefix = d.FilePath != null ? $"{d.FilePath}: " : "";
                return $"[expression-desugar] [{d.Severity}] {prefix}{d.Code}: {d.Message}";
            })
            .ToList();

        if (verbose)
            lines.AddRange(BuildVerboseInventoryLines());

        return lines;
    }

    /// <summary>
    /// Verbose "new-syntax inventory" section: guard buckets, the whole-tree
    /// gate balance and the one-line claimed-exclusions list (syntax owned by
    /// other passes and deliberately not counted here).
    /// </summary>
    private IReadOnlyList<string> BuildVerboseInventoryLines()
    {
        var c = _statistics.CollectionExpr;
        var gate = _gateResult;
        var lines = new List<string>
        {
            "[expression-desugar] new-syntax inventory:",
            $"[expression-desugar]   static-abstract: {c.GuardStaticAbstractDeclarations} " +
                $"declaration site(s), {c.GuardStaticAbstractCallSites} call/use site(s)",
            $"[expression-desugar]   generic-attribute: {c.GuardGenericAttributeDeclarations} " +
                $"declaration site(s), {c.GuardGenericAttributeApplications} application site(s)",
            $"[expression-desugar]   scoped: {c.GuardScopedParameterSites} parameter " +
                $"site(s) (Info; C01 strips downstream), {c.GuardScopedLocalSites} local/other site(s)",
            gate == null
                ? "[expression-desugar]   whole-tree residual gate: not run"
                : $"[expression-desugar]   whole-tree residual gate: " +
                  $"{gate.ResidualNodes} residual node(s) vs {gate.PairedNodes} paired " +
                  $"(balanced={gate.Balanced})",
            "[expression-desugar]   claimed exclusions (owned elsewhere, not counted): " +
                "null-forgiving 'x!' (C12); '#nullable'/'#pragma' directives (C01); " +
                "primary constructors (C15); raw string literals and 'u8' UTF8 string " +
                "literals; finalizers",
        };
        return lines;
    }
}
