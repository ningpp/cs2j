using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using CSharpToJava.Core.ExpressionDesugar.Interpolations;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

    /// <summary>C24 whole-tree gate result; null when the C24 rule is disabled.</summary>
    private InitializerTreeGateResult? _initializerGateResult;

    /// <summary>C23 whole-tree gate result; null when the C23 rule is disabled.</summary>
    private InterpolationTreeGateResult? _interpolationGateResult;

    public ExpressionDesugarPassAdapter(
        ExpressionDesugarOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        bool memberDesugarChainAvailable = false)
    {
        // When member-desugar (C11/C27) runs later in the chain it widens
        // init-only accessors to set methods and lifts preserved object
        // initializers (PD-8); tell the C24 rule so init-only CS2J0531 sites
        // downgrade to Warnings instead of failing the chain strictly.
        _options = memberDesugarChainAvailable && options.Initializers is { } initializers
            ? new ExpressionDesugarOptions
            {
                Interpolations = options.Interpolations,
                CollectionExpr = options.CollectionExpr,
                Initializers = new InitializerOptions
                {
                    Strict = initializers.Strict,
                    Scope = initializers.Scope,
                    RewriteInExpressionTrees = initializers.RewriteInExpressionTrees,
                    Unsupported = initializers.Unsupported,
                    MemberDesugarChainAvailable = true,
                },
                NullConditional = options.NullConditional,
            }
            : options;
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

    /// <summary>
    /// Opt-in: the per-file rewrite is a pure function — the shared
    /// <see cref="ExpressionDesugarPreprocessor"/> holds no mutable state (each
    /// call builds its own statistics/plans and an AdhocWorkspace), so the spine
    /// may run the read + rewrite step in parallel. Every accumulation
    /// (statistics, the per-file statistics map, diagnostics, verbose line) is
    /// deferred to <see cref="MergeFilePureResult"/>, which the runner calls
    /// serially in source-file order — keeping summary counts, the per-file map
    /// and diagnostic/verbose order identical to the sequential path.
    /// </summary>
    public override bool SupportsParallelFileRewrite => true;

    public override async Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        // Single source of truth for both spines: rewrite purely, then merge.
        var pure = await RewriteFilePureAsync(context);
        MergeFilePureResult(pure);
        return pure.Rewrite;
    }

    /// <summary>
    /// Pure per-file rewrite for the opt-in parallel path: reads only immutable
    /// adapter state and calls the stateless preprocessor; no shared field is
    /// mutated, so it is safe to call concurrently. Per-file effects travel
    /// through <see cref="PassFilePureResult.PassState"/> and are applied later by
    /// <see cref="MergeFilePureResult"/>.
    /// </summary>
    public override Task<PassFilePureResult> RewriteFilePureAsync(PassFileContext context)
    {
        var result = _preprocessor.Preprocess(
            context.SourceCode, context.SemanticModel, _options);
        PassFileRewrite? rewrite = result.Changed
            ? new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
            }
            : null;
        return Task.FromResult(new PassFilePureResult
        {
            Context = context,
            Rewrite = rewrite,
            PassState = result,
        });
    }

    /// <summary>
    /// Serial merge of one file's pure result, invoked by the runner in
    /// source-file order. Applies statistics / the per-file statistics map /
    /// diagnostics and prints the verbose line here (not in the parallel phase)
    /// so its order is preserved.
    /// </summary>
    public override void MergeFilePureResult(PassFilePureResult pure)
    {
        var context = pure.Context;
        var result = (ExpressionDesugarResult)pure.PassState!;
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
    }

    /// <summary>
    /// M3 CE-12 whole-tree gate: re-parse every intermediate .cs AS WRITTEN,
    /// count residual collection/spread nodes, and reconcile against the
    /// per-file paired skip accounting. Mismatch -> CS2J0589 Error makes the
    /// chain fail (covers verbatim-copied dirty files too); a balanced run
    /// emits CS2J0584 Info (verbose display only).
    ///
    /// All three rule gates (C36 CE-12, C23, C24 IN-13) share ONE traversal
    /// here: a clean-skipped file (no rule was active) provably carries no
    /// residual trigger node and no pairing, so it is excluded from the walk
    /// entirely — the pass used to re-read + re-parse + walk every intermediate
    /// file twice, which dominated the pass on large trees with few dirty files.
    /// </summary>
    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        var residualByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pairedByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // C23 (only populated when the interpolation rule is enabled): keeps
        // Enabled=false byte-for-byte identical — no interpolation nodes are
        // ever counted when the rule is off (design §7.5 zero-interpolation
        // 口径 applies to opt-in runs only).
        var interpolationEnabled = _options.Interpolations.Enabled;
        var interpResidualByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var interpPairedByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // C24 (IN-13) additive gate: same opt-in discipline (subsection present).
        var initializerEnabled = _options.Initializers is not null;
        var initResidualByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var initPairedByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

            _fileStatistics.TryGetValue(relativePath, out var fileStats);

            pairedByFile[relativePath] = fileStats is null
                ? 0
                : CollectionExprTreeGate.CountPairedNodes(fileStats.CollectionExpr);
            if (interpolationEnabled)
            {
                interpPairedByFile[relativePath] = fileStats is null
                    ? 0
                    : fileStats.Interpolations.CountPairedNodes();
            }

            if (initializerEnabled)
            {
                initPairedByFile[relativePath] = fileStats is null
                    ? 0
                    : InitializerTreeGate.CountPairedNodes(fileStats.Initializers);
            }

            // A clean-skipped file had no rule active, so its verbatim copy
            // cannot contain a residual trigger node of any rule kind, and its
            // pairing accounting is empty: skip the read + parse + walk.
            if (fileStats is { FilesSkippedClean: 1 }
                && pairedByFile[relativePath] == 0
                && (!interpolationEnabled || interpPairedByFile[relativePath] == 0)
                && (!initializerEnabled || initPairedByFile[relativePath] == 0))
            {
                continue;
            }

            var source = File.ReadAllText(intermediateFile);
            var root = CSharpSyntaxTree.ParseText(source, parseOptions).GetCompilationUnitRoot();

            // Single traversal computes all three residual counts.
            var residual = 0;
            var interpResidual = 0;
            var initResidual = 0;
            foreach (var node in root.DescendantNodes())
            {
                if (CollectionExprSyntaxFacts.IsResidualNode(node))
                    residual++;
                if (interpolationEnabled && InterpolationSyntaxFacts.IsResidualNode(node))
                    interpResidual++;
                if (initializerEnabled && InitializerSyntaxFacts.IsResidualNode(node))
                    initResidual++;
            }

            if (residual > 0)
                residualByFile[relativePath] = residual;
            if (interpResidual > 0)
                interpResidualByFile[relativePath] = interpResidual;
            if (initResidual > 0)
                initResidualByFile[relativePath] = initResidual;
        }

        _gateResult = CollectionExprTreeGate.Reconcile(residualByFile, pairedByFile);

        if (interpolationEnabled)
            _interpolationGateResult =
                InterpolationTreeGate.Reconcile(interpResidualByFile, interpPairedByFile);

        if (initializerEnabled)
            _initializerGateResult =
                InitializerTreeGate.Reconcile(initResidualByFile, initPairedByFile);

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

        // ---- C23 whole-tree gate (opt-in runs only; never runs when off) ----
        var interpGate = _interpolationGateResult;
        if (interpGate is { Balanced: false })
        {
            // Always Error at tree level (like CS2J0589): an unpaired
            // interpolation on disk means some site silently escaped both
            // expansion and the explained-preserve accounting.
            var unpaired = string.Join(
                ", ",
                interpGate.UnpairedFiles.Select(f =>
                    $"{f.File} ({f.ResidualNodes} residual/{f.PairedNodes} paired)"));
            _diagnostics.Add(new ExpressionDesugarDiagnostic(
                ExpressionDesugarSeverity.Error,
                InterpolationDiagnosticCodes.CS2J0516,
                string.Format(
                    InterpolationDiagnostics.TreeResidualMessage,
                    interpGate.ResidualNodes,
                    interpGate.PairedNodes,
                    unpaired.Length == 0 ? "?" : unpaired)));
        }
        else if (_verbose && interpGate != null)
        {
            _diagnostics.Add(new ExpressionDesugarDiagnostic(
                ExpressionDesugarSeverity.Info,
                InterpolationDiagnosticCodes.CS2J0517,
                string.Format(
                    InterpolationDiagnostics.TreeInventoryMessage,
                    interpGate.ResidualNodes,
                    interpGate.PairedNodes)));
        }

        // ---- C24 IN-13 additive whole-tree gate (subsection present only) ----
        // Mismatch -> CS2J0545 Error fails the chain; balanced + verbose emits
        // CS2J0546 Info.
        var initGate = _initializerGateResult;
        if (initGate is { Balanced: false })
        {
            var unpaired = string.Join(
                ", ",
                initGate.UnpairedFiles.Select(f =>
                    $"{f.File} ({f.ResidualNodes} residual/{f.PairedNodes} paired)"));
            _diagnostics.Add(new ExpressionDesugarDiagnostic(
                ExpressionDesugarSeverity.Error,
                InitializerDiagnosticCodes.CS2J0545,
                string.Format(
                    InitializerDiagnostics.TreeResidualMessage,
                    initGate.ResidualNodes,
                    initGate.PairedNodes,
                    unpaired.Length == 0 ? "?" : unpaired)));
        }
        else if (_verbose && initGate != null)
        {
            _diagnostics.Add(new ExpressionDesugarDiagnostic(
                ExpressionDesugarSeverity.Info,
                InitializerDiagnosticCodes.CS2J0546,
                string.Format(
                    InitializerDiagnostics.InventorySummaryMessage,
                    initGate.ResidualNodes,
                    initGate.PairedNodes)));
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
        var line = $"expression-desugar: {_statistics.FilesTransformed} transformed, " +
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

        // C23 segment is appended ONLY when the rule was enabled: the opt-in
        // red line is zero observable change (incl. this line's text) while
        // the rule stays off.
        if (_options.Interpolations.Enabled)
            line += "; " + BuildInterpolationSummarySegment();

        // C24 (IN-13) additive init segment: emitted only when the rule is
        // active (Initializers subsection present), keeping the C36-only
        // summary byte-identical on default chains.
        if (_options.Initializers is not null)
            line += "; " + BuildInitializerSummarySegment();

        // C35 additive segment: same opt-in discipline; absent byte-for-byte
        // until the NullConditional subsection is present.
        if (_options.NullConditional is not null)
            line += "; " + BuildNullConditionalSummarySegment();

        return line;
    }

    /// <summary>
    /// C23 summary segment (design §5.5): sites/concat/format (with the
    /// alignment/formatClause/iformattable sub-buckets)/fold/formattable-kept/
    /// exprtree-skip/handler err|keep/temps (expected 0)/residual.
    /// </summary>
    private string BuildInterpolationSummarySegment()
    {
        var i = _statistics.Interpolations;
        var handlerErrors = _diagnostics.Count(
            d => d.Code == InterpolationDiagnosticCodes.CS2J0510
                 && d.Severity == ExpressionDesugarSeverity.Error);
        var handlerKept = _diagnostics.Count(
            d => d.Code == InterpolationDiagnosticCodes.CS2J0510
                 && d.Severity != ExpressionDesugarSeverity.Error);

        return $"interp: {i.InterpolationSites} sites, concat {i.ConcatExpansions}, " +
               $"format {i.FormatExpansions} (align {i.FormatByAlignment}" +
               $"/fmt {i.FormatByFormatClause}" +
               $"/iformattable {i.FormatByIFormattableUpgrade}), " +
               $"fold {i.ConstantsFolded}, " +
               $"formattable-kept {i.KeptFormattableStringSites}, " +
               $"exprtree-skip {i.ExpressionTreeSitesSkipped}, " +
               $"handler err{handlerErrors}|keep{handlerKept}, " +
               $"temps {i.TempsLifted}, residual {i.ResidualInterpolationCount}";
    }

    /// <summary>
    /// C24 summary segment (design §5-2): site/Add/nesting/lowering buckets plus
    /// the whole-tree gate residual. Non-strict preserved sites (Error-class
    /// codes downgraded to Warning) are listed distinctly so a dual-track
    /// consumer never mistakes them for lowered ones (design §6 / 裁决 5).
    /// </summary>
    private string BuildInitializerSummarySegment()
    {
        var i = _statistics.Initializers;
        var skipped = i.SkippedByReason.Values.Sum();
        var nonStrictKept = 0;
        foreach (var code in NonStrictKeptCodes)
        {
            i.SkippedByReason.TryGetValue(code, out var count);
            nonStrictKept += count;
        }

        var residual = _initializerGateResult?.ResidualNodes ?? i.ResidualInitializerCount;
        return $"init: {i.ObjectInitializerSites} obj, " +
               $"{i.CollectionInitializerSites} coll " +
               $"(single {i.AddSingle}/multi {i.AddMulti}/ext {i.AddExtension}" +
               $"/params {i.AddParams}/fluent {i.AddFluent}), " +
               $"{i.DictionaryIndexInitializerSites} dict-index, " +
               $"{i.TargetTypedNewSites} target-new, " +
               $"{i.NestedCreateSites}/{i.NestedMemberInitSites} nested, " +
               $"{i.ContextLoweredSites} ctx-stmts, {i.TempsLifted} temps, " +
               $"{skipped} skipped ({nonStrictKept} non-strict kept), " +
               $"{residual} residual";
    }

    /// <summary>Error-class codes whose non-strict downgrade keeps the node.</summary>
    private static readonly string[] NonStrictKeptCodes =
    {
        InitializerDiagnosticCodes.CS2J0531,
        InitializerDiagnosticCodes.CS2J0533,
        InitializerDiagnosticCodes.CS2J0535,
        InitializerDiagnosticCodes.CS2J0537,
        InitializerDiagnosticCodes.CS2J0538,
        InitializerDiagnosticCodes.CS2J0541,
    };

    /// <summary>
    /// C35 summary segment (design §6): sites by terminal (member/call, index,
    /// invoke split event/delegate), void/inlined/anchor buckets, lifted value
    /// terminals, E03 coalesce compositions, C51 deferrals, temps, skips and
    /// the residual gate count, followed by the additive C51 assign= clause.
    /// </summary>
    private string BuildNullConditionalSummarySegment()
    {
        var n = _statistics.NullConditional;
        var skipped = n.SkippedByReason.Values.Sum();

        var line = $"nc: {n.ConditionalAccessSites} member/call, " +
               $"{n.ConditionalIndexSites} idx, " +
               $"{n.InvokeSites} invoke (event {n.EventSites}" +
               $"/delegate {n.DelegateSites}), " +
               $"{n.VoidStatementSites} void, {n.InlinedSites} inline, " +
               $"{n.LiftedValueTypeSites} lifted, " +
               $"{n.CoalesceCompositionSites} coalesce, " +
               $"{n.DeferredForC51Sites} deferred-c51, " +
               $"{n.SegmentTempsLifted} temps, " +
               $"{skipped} skipped, {n.ResidualNodeCount} residual";

        // C51 additive clause: present (like the whole nc segment) only while
        // the NullConditional subsection is enabled; zero-site runs keep the
        // clause but with zeroed buckets, staying idempotent across re-runs.
        line += $", assign= {n.AssignmentMemberSites} member" +
                $"/{n.AssignmentIndexSites} idx, " +
                $"{n.CompoundAssignmentSites} compound" +
                $"/{n.EventCompoundSites} event, " +
                $"{n.CoalesceAssignmentSites} coalesce, " +
                $"{n.ValueFormAssignmentSites} value, " +
                $"{n.AssignmentChainSites} chain, " +
                $"{n.AssignmentRhsConditionalSites} rhs-nc, " +
                $"{n.AssignmentReceiverTempsLifted} temps, " +
                $"{n.AssignmentResidualNodeCount} residual";

        return line;
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

        if (_options.Initializers is not null)
        {
            var initGate = _initializerGateResult;
            lines.Add(
                initGate == null
                    ? "[expression-desugar]   init whole-tree residual gate: not run"
                    : $"[expression-desugar]   init whole-tree residual gate: " +
                      $"{initGate.ResidualNodes} residual node(s) vs " +
                      $"{initGate.PairedNodes} paired (balanced={initGate.Balanced})");
            lines.Add("[expression-desugar]   " + BuildInitializerSummarySegment());
        }

        return lines;
    }
}
