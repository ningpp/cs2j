using CSharpToJava.Core.ExtensionMethodResolve.Declarations;
using CSharpToJava.Core.IndexRange;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Core entry for the extension-method-resolve pass (C19): bind the
/// whole-tree compilation once, analyze reduced extension sites per syntax
/// tree, rewrite each file, assert expression-tree identity with per-site
/// rollback, and expose the residual scan. No cross-file generated products.
///
/// M2 adds EM-REF, EM-GROUP capture lambdas, EM-COND single-evaluation
/// lifting and EM-EXPRTREE identity assertion. Files with no rewritten node
/// return Changed=false (runner byte-copies, BOM preserved). Lift-heavy files
/// use whole-file whitespace normalization; ordinary files keep the
/// three-level region formatting.
/// </summary>
public sealed class ExtensionMethodResolvePreprocessor
{
    private const int MaxIdentityAttempts = 3;

    private sealed class FilePlan
    {
        public required IReadOnlyList<ExtensionCallBinding> Bindings { get; init; }
        public required IReadOnlyList<ExtensionConditionalPlan> ConditionalPlans { get; init; }
        public required IReadOnlyList<ExtensionGroupPlan> GroupPlans { get; init; }
        public required IReadOnlyList<InvocationExpressionSyntax> RefMismatchSites { get; init; }
        public required IReadOnlyList<InvocationExpressionSyntax> ExprTreeReportOnlySites { get; init; }
        public required IReadOnlyList<InvocationExpressionSyntax> MixedChainSkippedSites { get; init; }
        public required IReadOnlyList<InvocationExpressionSyntax> OverrideSkippedSites { get; init; }
        public required IReadOnlyList<ExtensionDiagnostic> Diagnostics { get; init; }
        public required ExtensionMethodResolveStatistics Statistics { get; init; }

        /// <summary>C49 block analysis for this tree when materialization runs; null otherwise.</summary>
        public BlockAnalysisResult? BlockAnalysis { get; init; }

        /// <summary>CS2J1213/CS2J1208 diagnostics produced at the analyze entry (before block analysis).</summary>
        public required IReadOnlyList<ExtensionDiagnostic> BlockEntryDiagnostics { get; init; }
    }

    private Compilation? _compilation;
    private ExtensionMethodResolveOptions _options = new();
    private readonly Dictionary<SyntaxTree, FilePlan> _plans = new();
    private readonly Dictionary<SyntaxTree, BlockAnalysisResult> _blockAnalyses = new();
    private readonly HashSet<string> _rewrittenFiles = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ExtensionRewrittenSiteKey> _rewrittenSites = Array.Empty<ExtensionRewrittenSiteKey>();
    private IReadOnlyList<ExtensionExemptSite> _exemptSites = Array.Empty<ExtensionExemptSite>();
    private int _thisModifierBaseline;

    /// <summary>Test hook: force an identity mismatch for a rewritten expression-tree site.</summary>
    internal Func<InvocationExpressionSyntax, IMethodSymbol, bool>? ForceExpressionTreeMismatch { get; set; }

    /// <summary>
    /// M4 test/drill hook (design §4.4-4): force the given ORIGINAL bound
    /// invocation to be replaced by an expression guaranteed not to compile,
    /// so the adapter-level candidate-error attribution + file rollback can
    /// be exercised deterministically.
    /// </summary>
    internal Func<InvocationExpressionSyntax, IMethodSymbol, bool>? ForceBrokenRewrite { get; set; }

    /// <summary>Block statistics from the most recent <see cref="RewriteFile"/> call (null when no block pass ran).</summary>
    public ExtensionBlockStatistics? BlockStatisticsForLastFile { get; private set; }

    /// <summary>Absolute paths of files whose rewrite output was Changed=true in the last Analyze/Rewrite cycle.</summary>
    public IReadOnlyCollection<string> RewrittenFiles => _rewrittenFiles;

    /// <summary>
    /// Per-tree C49 block analyses from the last <see cref="Analyze"/> cycle
    /// (only trees where materialization actually ran). Consumed by the block
    /// residual scan; empty when block materialization is off.
    /// </summary>
    internal IReadOnlyDictionary<SyntaxTree, BlockAnalysisResult> BlockAnalyses => _blockAnalyses;

    /// <summary>M4 gate 5: extension-method <c>this</c> modifiers counted in the original compilation.</summary>
    public int ThisModifierBaseline => _thisModifierBaseline;

    public void BindCompilation(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
    }

    /// <summary>Whole-tree analysis: per-file binding/plan tables, sorted deterministically by file path + span.</summary>
    public void Analyze(ExtensionMethodResolveOptions options)
        => Analyze(options, blockOptions: null, memberDesugarChainAvailable: true);

    /// <summary>
    /// Whole-tree analysis with C49 extension-block policy.
    /// <paramref name="blockOptions"/> null keeps the classic behavior: every
    /// extension block hits CS2J1206 and nothing about the classic pass
    /// changes. When <see cref="ExtensionBlockOptions.MaterializeExtensionBlocks"/>
    /// is true, the parse self-check (CS2J1213), the property chain gate
    /// (CS2J1208), and the block analysis run, and the classic block gate is
    /// suppressed for materialized trees.
    /// </summary>
    public void Analyze(
        ExtensionMethodResolveOptions options,
        ExtensionBlockOptions? blockOptions,
        bool memberDesugarChainAvailable)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        _options = BuildEffectiveOptions(options);
        _plans.Clear();
        _blockAnalyses.Clear();
        _rewrittenFiles.Clear();
        _thisModifierBaseline = 0;
        BlockStatisticsForLastFile = null;

        foreach (var tree in _compilation.SyntaxTrees.OrderBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var model = _compilation.GetSemanticModel(tree);
            var entryDiagnostics = new List<ExtensionDiagnostic>();
            BlockAnalysisResult? blockAnalysis = null;
            var suppressClassicBlockGate = false;

            var materialize = blockOptions?.MaterializeExtensionBlocks == true;
            if (materialize)
            {
                var root = tree.GetCompilationUnitRoot();
                var hasBlocks = root.DescendantNodes()
                    .Any(n => n.IsKind(SyntaxKind.ExtensionBlockDeclaration));

                if (hasBlocks)
                {
                    if (!ExtensionBlockParseProbe.Passes())
                    {
                        // C49 cannot run: keep the classic 1206 gate, add 1213 per block.
                        foreach (var block in ExtensionBlockSyntaxFacts.EnumerateBlocks(root))
                        {
                            entryDiagnostics.Add(BlockEntryDiagnostic(
                                ExtensionBlockCodes.ParseProbeFailed,
                                ExtensionBlockMessages.ParseProbeFailed,
                                tree, block));
                        }
                    }
                    else if (blockOptions!.EnableProperties && !memberDesugarChainAvailable)
                    {
                        // Property chain missing: take over the gate with 1208.
                        suppressClassicBlockGate = true;
                        foreach (var block in ExtensionBlockSyntaxFacts.EnumerateBlocks(root))
                        {
                            entryDiagnostics.Add(BlockEntryDiagnostic(
                                ExtensionBlockCodes.PropertyChainGate,
                                ExtensionBlockMessages.PropertyChainGate,
                                tree, block));
                        }
                    }
                    else
                    {
                        suppressClassicBlockGate = true;
                        var analyzer = new ExtensionBlockAnalyzer(model, blockOptions!);
                        blockAnalysis = analyzer.Analyze();
                        _blockAnalyses[tree] = blockAnalysis;
                    }
                }
            }

            var binder = new ExtensionCallBinder(model, _options, suppressClassicBlockGate);
            binder.Analyze();

            _thisModifierBaseline += ExtensionMethodResidualScanner.CountExtensionThisModifiers(tree);

            _plans[tree] = new FilePlan
            {
                Bindings = binder.Bindings,
                ConditionalPlans = binder.ConditionalPlans,
                GroupPlans = binder.GroupPlans,
                RefMismatchSites = binder.RefMismatchSites,
                ExprTreeReportOnlySites = binder.ExprTreeReportOnlySites,
                MixedChainSkippedSites = binder.MixedChainSkippedSites,
                OverrideSkippedSites = binder.OverrideSkippedSites,
                Diagnostics = binder.Diagnostics,
                Statistics = binder.Statistics,
                BlockAnalysis = blockAnalysis,
                BlockEntryDiagnostics = entryDiagnostics,
            };
        }

        _rewrittenSites = Array.Empty<ExtensionRewrittenSiteKey>();
        _exemptSites = Array.Empty<ExtensionExemptSite>();
    }

    private static ExtensionDiagnostic BlockEntryDiagnostic(
        string code,
        string message,
        SyntaxTree tree,
        SyntaxNode site)
        => new(
            ExtensionSeverity.Error,
            ExtensionRule.Ext14,
            code,
            message,
            tree.FilePath,
            site.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

    /// <summary>
    /// M4 EM-LINQ invariant: the protected LINQ hosts cannot be removed from
    /// <see cref="ExtensionMethodResolveOptions.LinqHostSet"/>; extra hosts
    /// registered via <see cref="ExtensionHostOverrides.AdditionalLinqHosts"/>
    /// are unioned in (extend-only).
    /// </summary>
    private static ExtensionMethodResolveOptions BuildEffectiveOptions(ExtensionMethodResolveOptions options)
    {
        foreach (var protectedHost in ExtensionHostOverrides.ProtectedLinqHosts)
        {
            if (!options.LinqHostSet.Contains(protectedHost))
            {
                throw new InvalidOperationException(
                    "Extension-methods LinqHostSet is extend-only: protected host '" + protectedHost
                    + "' (System.Linq.Enumerable/System.Linq.Queryable, E16/C26 ownership) cannot be removed.");
            }
        }

        var additions = options.HostOverrides?.AdditionalLinqHosts;
        if (additions is null || additions.Count == 0)
        {
            return options;
        }

        var effective = new HashSet<string>(options.LinqHostSet, StringComparer.Ordinal);
        effective.UnionWith(additions);
        return options with { LinqHostSet = effective };
    }

    /// <summary>Rewrite the syntax tree backing <paramref name="model"/> (the same tree analyzed in <see cref="Analyze"/>).</summary>
    public ExtensionFileResult RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        ArgumentNullException.ThrowIfNull(model);

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

        if (!_plans.TryGetValue(model.SyntaxTree, out var plan))
        {
            return new ExtensionFileResult(
                root.ToFullString(),
                Changed: false,
                Array.Empty<ExtensionDiagnostic>(),
                new ExtensionMethodResolveStatistics { FilesScanned = 1, FilesClean = 1 });
        }

        var stats = plan.Statistics;
        stats.FilesScanned++;

        var liftableConditions = plan.ConditionalPlans.Where(p => p.Liftable).ToList();
        var rewritableGroups = plan.GroupPlans.Where(g => g.Rewritable).ToList();
        var tempGroups = rewritableGroups.Where(g => !g.DirectCapture).ToList();

        var blockAnalysis = plan.BlockAnalysis;
        var blockPlans = blockAnalysis?.BlockPlans ?? Array.Empty<BlockPlan>();
        var blockSiteCount = blockAnalysis is null
            ? 0
            : blockAnalysis.MethodSites.Count
              + blockAnalysis.ReadSites.Count
              + blockAnalysis.WriteSites.Count
              + blockAnalysis.CompoundSites.Count
              + blockAnalysis.ConditionalPlans.Count;

        var extraDiagnostics = plan.BlockEntryDiagnostics
            .Concat(blockAnalysis is null
                ? Array.Empty<ExtensionDiagnostic>()
                : blockAnalysis.Diagnostics)
            .ToList();

        if (plan.Bindings.Count == 0
            && liftableConditions.Count == 0
            && rewritableGroups.Count == 0
            && blockPlans.Count == 0
            && blockSiteCount == 0)
        {
            // 1204/1205/1207/1208/1213-only file: exempts must still pair with
            // the residual gates on the byte-copied tree.
            FinalizeSiteKeys(model.SyntaxTree, plan, liftableConditions, rewritableGroups, new HashSet<SyntaxNode>());
            FinalizeExemptSites(model.SyntaxTree, plan, new HashSet<SyntaxNode>());
            stats.FilesClean++;
            BlockStatisticsForLastFile = blockAnalysis?.Statistics;
            return new ExtensionFileResult(
                root.ToFullString(),
                Changed: false,
                plan.Diagnostics.Concat(extraDiagnostics).ToList(),
                stats);
        }

        var rollback = new HashSet<SyntaxNode>();
        var identityDiagnostics = new List<ExtensionDiagnostic>();
        CompilationUnitSyntax? newRoot = null;
        IReadOnlyList<ExpressionTreeSiteMarker> etSites = Array.Empty<ExpressionTreeSiteMarker>();

        for (var attempt = 0; attempt < MaxIdentityAttempts; attempt++)
        {
            var output = RewriteOnce(root, plan, liftableConditions, rewritableGroups, tempGroups, rollback);
            newRoot = output.Root;
            etSites = output.ExpressionTreeSites;

            var mismatches = VerifyExpressionTreeIdentity(newRoot, model.SyntaxTree, etSites);
            if (mismatches.Count == 0)
            {
                break;
            }

            foreach (var mismatch in mismatches)
            {
                rollback.Add(mismatch);
                identityDiagnostics.Add(new ExtensionDiagnostic(
                    _options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning,
                    ExtensionRule.ExprTree,
                    ExtensionMethodCodes.ExpressionTreeIdentityFailed,
                    "Expression-tree identity assertion failed for extension call '"
                    + mismatch.ToString() + "'; the single site rolls back to the original sugar.",
                    model.SyntaxTree.FilePath,
                    mismatch.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
        }

        stats.ExpressionTreeRollbacks += rollback.Count;

        var hasRewrittenNode = newRoot!
            .GetAnnotatedNodes(ExtensionCallRewriter.RewrittenAnnotation)
            .Any();
        var blocksGone = blockPlans.Count > 0
                         && !newRoot!.DescendantNodes().Any(n => n.IsKind(SyntaxKind.ExtensionBlockDeclaration));
        if (!hasRewrittenNode && !blocksGone)
        {
            // No rewrite (e.g. 1204/1205/1207-only files): exempts must still
            // pair with the residual gates on the byte-copied tree.
            FinalizeSiteKeys(model.SyntaxTree, plan, liftableConditions, rewritableGroups, rollback);
            FinalizeExemptSites(model.SyntaxTree, plan, rollback);
            stats.FilesClean++;
            BlockStatisticsForLastFile = blockAnalysis?.Statistics;
            return new ExtensionFileResult(
                root.ToFullString(),
                Changed: false,
                plan.Diagnostics.Concat(extraDiagnostics).Concat(identityDiagnostics).ToList(),
                stats);
        }

        FinalizeSiteKeys(model.SyntaxTree, plan, liftableConditions, rewritableGroups, rollback);
        FinalizeExemptSites(model.SyntaxTree, plan, rollback);

        stats.FilesTransformed++;
        stats.CallsitesRewritten += CountRewrittenInvocations(newRoot!, plan, liftableConditions, rewritableGroups, rollback);
        stats.GenericHostQualified += CountGenericHosts(plan, liftableConditions, rewritableGroups, rollback);
        stats.RefReceiverSitesEmitted += plan.Bindings
            .Count(b => !rollback.Contains(b.Invocation) && b.ReceiverRefKind != RefKind.None);

        var needsWholeFileFormat = liftableConditions.Count > 0
            || tempGroups.Count > 0
            // Block members are built from trivia-stripped pieces; every
            // materialized file gets whole-file normalization so generated
            // declarations and spliced statements stay syntactically clean.
            || blockAnalysis is not null;
        var outputCode = Format(newRoot!, needsWholeFileFormat);
        _rewrittenFiles.Add(model.SyntaxTree.FilePath);
        BlockStatisticsForLastFile = blockAnalysis?.Statistics;
        return new ExtensionFileResult(
            outputCode,
            Changed: true,
            plan.Diagnostics.Concat(extraDiagnostics).Concat(identityDiagnostics).ToList(),
            stats);
    }

    private sealed record RewriteOutput(
        CompilationUnitSyntax Root,
        IReadOnlyList<ExpressionTreeSiteMarker> ExpressionTreeSites);

    private RewriteOutput RewriteOnce(
        CompilationUnitSyntax root,
        FilePlan plan,
        IReadOnlyList<ExtensionConditionalPlan> liftableConditions,
        IReadOnlyList<ExtensionGroupPlan> rewritableGroups,
        IReadOnlyList<ExtensionGroupPlan> tempGroups,
        HashSet<SyntaxNode> rollback)
    {
        // 1) Anchor/site annotations survive every subsequent rewrite.
        var anchorNodes = new Dictionary<SyntaxNode, SyntaxAnnotation>();
        var siteAnnotations = new List<(SyntaxNode Node, SyntaxAnnotation Annotation)>();

        foreach (var condition in liftableConditions)
        {
            siteAnnotations.Add((condition.OuterConditional, condition.SiteAnnotation));
            if (!anchorNodes.ContainsKey(condition.Anchor.Node))
            {
                anchorNodes[condition.Anchor.Node] = condition.AnchorAnnotation;
            }
        }

        // C49 synthetic conditional plans share the same anchor/site machinery.
        var blockLiftableConditions = plan.BlockAnalysis?.ConditionalPlans
            .Where(p => p.Liftable)
            .ToList() ?? new List<ExtensionConditionalPlan>();
        foreach (var condition in blockLiftableConditions)
        {
            siteAnnotations.Add((condition.OuterConditional, condition.SiteAnnotation));
            if (!anchorNodes.ContainsKey(condition.Anchor.Node))
            {
                anchorNodes[condition.Anchor.Node] = condition.AnchorAnnotation;
            }
        }

        foreach (var group in tempGroups)
        {
            var anchor = group.Anchor!.Node;
            if (!anchorNodes.ContainsKey(anchor))
            {
                anchorNodes[anchor] = group.AnchorAnnotation!;
            }
        }

        // Per-site annotations let the rewriter recognize nodes even when an
        // anchor replacement (e.g. a statement hosting a temp-captured group)
        // has recreated all descendant node instances.
        var bindingAnnotations = new Dictionary<SyntaxAnnotation, ExtensionCallBinding>();
        var siteAnnotationByBinding = new Dictionary<SyntaxNode, SyntaxAnnotation>();
        foreach (var binding in plan.Bindings.Where(b => !rollback.Contains(b.Invocation)))
        {
            var annotation = new SyntaxAnnotation("C19", "ExtensionBindingSite");
            bindingAnnotations[annotation] = binding;
            siteAnnotationByBinding[binding.Invocation] = annotation;
        }

        var groupAnnotations = new Dictionary<SyntaxAnnotation, ExtensionGroupPlan>();
        var siteAnnotationByGroup = new Dictionary<SyntaxNode, SyntaxAnnotation>();
        foreach (var group in rewritableGroups)
        {
            var annotation = new SyntaxAnnotation("C19", "ExtensionGroupSite");
            groupAnnotations[annotation] = group;
            siteAnnotationByGroup[group.MemberAccess] = annotation;
        }

        var blockTargets = BuildBlockAnnotationTargets(plan.BlockAnalysis);

        var annotationTargets = siteAnnotations
            .Select(pair => (pair.Node, pair.Annotation))
            .Concat(anchorNodes.Select(pair => (pair.Key, pair.Value)))
            .Concat(siteAnnotationByBinding.Select(pair => (pair.Key, pair.Value)))
            .Concat(siteAnnotationByGroup.Select(pair => (pair.Key, pair.Value)))
            .Concat(blockTargets)
            .ToList();

        var annotatedRoot = (CompilationUnitSyntax)root.ReplaceNodes(
            annotationTargets.Select(t => t.Item1),
            (original, rewritten) =>
            {
                // An anchor can be an ancestor of a site node: use the
                // bottom-up REWRITTEN node so descendant annotations survive.
                var target = annotationTargets.First(t => t.Item1 == original);
                return rewritten.WithAdditionalAnnotations(target.Item2);
            });

        // 2) Main expression rewrite: ordinary calls + group lambdas.
        var bindingsByNode = plan.Bindings
            .Where(b => !rollback.Contains(b.Invocation))
            .ToDictionary(b => (SyntaxNode)b.Invocation, b => b);
        var groupsByNode = rewritableGroups
            .ToDictionary(g => (SyntaxNode)g.MemberAccess, g => g);

        var rewriter = new ExtensionCallRewriter(
            bindingsByNode, groupsByNode, rollback,
            bindingAnnotations, groupAnnotations, ForceBrokenRewrite);
        var rootAfterRewrite = (CompilationUnitSyntax?)rewriter.Visit(annotatedRoot) ?? annotatedRoot;

        // 2b) C49: block call sites + property seam, then block declarations.
        if (plan.BlockAnalysis is not null)
        {
            var callSiteRewriter = new ExtensionBlockCallSiteRewriter(plan.BlockAnalysis);
            rootAfterRewrite = (CompilationUnitSyntax?)callSiteRewriter.Visit(rootAfterRewrite)
                               ?? rootAfterRewrite;

            var declarationRewriter = new ExtensionBlockDeclarationRewriter(
                _compilation!.GetSemanticModel(root.SyntaxTree),
                plan.BlockAnalysis.BlockPlans);
            rootAfterRewrite = (CompilationUnitSyntax?)declarationRewriter.Visit(rootAfterRewrite)
                               ?? rootAfterRewrite;
        }

        // 3) Null-conditional chain lowering (spine static calls + temps):
        //    classic plans + C49 synthetic plans with host overrides.
        var allLiftableConditions = liftableConditions.Concat(blockLiftableConditions).ToList();
        var conditionalOutput = ExtensionConditionalLowerer.Lower(
            rootAfterRewrite,
            allLiftableConditions,
            anchorNodes,
            plan.BlockAnalysis?.ConditionalHostOverrides);
        var current = conditionalOutput.Root;
        var pending = new List<TempLifter.PendingTemp>(conditionalOutput.Pending);

        // 4) Temp-captured group declarations.
        var groupOrder = pending.Count;
        var groupLambdaDecls = new Dictionary<SyntaxNode, List<StatementSyntax>>();
        foreach (var group in tempGroups.OrderBy(g => g.SpanStart))
        {
            if (!rewriter.GroupReceivers.TryGetValue(group, out var receiver))
            {
                continue;
            }

            var declaration = TempLifter.DeclareVar(group.TempName!, receiver);
            switch (group.Anchor!.Kind)
            {
                case ExtensionAnchorKind.Statement:
                case ExtensionAnchorKind.ArrowClause:
                    pending.Add(new TempLifter.PendingTemp(declaration, group.Anchor.Node, ++groupOrder));
                    break;
                case ExtensionAnchorKind.ExpressionLambda:
                    if (!groupLambdaDecls.TryGetValue(group.Anchor.Node, out var list))
                    {
                        list = new List<StatementSyntax>();
                        groupLambdaDecls[group.Anchor.Node] = list;
                    }

                    list.Add(declaration);
                    break;
            }
        }

        // 5) Void anchors leave no expression behind: replace them first so the
        //    shared anchor annotation is consumed only by their transform.
        current = ExtensionTempLift.ExpandVoidAnchors(current, conditionalOutput.VoidExpansions);

        // 6) Plain statement/arrow temp insertion (embedded wrapping, arrow
        //    expansion) via the shared index/range lifter.
        current = TempLifter.Apply(current, pending, anchorNodes);

        // 7) Expression-lambda expansions: conditional value chains, then groups.
        var lambdaExpansions = new List<LambdaExpansion>(conditionalOutput.LambdaExpansions);
        foreach (var (anchorNode, declarations) in groupLambdaDecls)
        {
            var existing = lambdaExpansions.FirstOrDefault(e => anchorNodes[anchorNode] == e.AnchorAnnotation);
            if (existing is not null)
            {
                // Cannot occur (a lambda has one body expression); keep both lists anyway.
                lambdaExpansions[lambdaExpansions.IndexOf(existing)] = existing with
                {
                    Declarations = existing.Declarations.Concat(declarations).ToList(),
                };
            }
            else
            {
                lambdaExpansions.Add(new LambdaExpansion(
                    anchorNodes[anchorNode],
                    declarations,
                    LambdaBodyMode.Return));
            }
        }

        current = ExtensionTempLift.ExpandExpressionLambdas(current, lambdaExpansions);

        return new RewriteOutput(current, rewriter.ExpressionTreeSites);
    }

    /// <summary>
    /// Every C49 node that must be recognized after tree rebuilds: block
    /// declarations, all member call sites (methods/reads/writes/compound
    /// statements), and property conditional seam bindings.
    /// </summary>
    private static List<(SyntaxNode, SyntaxAnnotation)> BuildBlockAnnotationTargets(
        BlockAnalysisResult? analysis)
    {
        if (analysis is null)
        {
            return new List<(SyntaxNode, SyntaxAnnotation)>();
        }

        var targets = new List<(SyntaxNode, SyntaxAnnotation)>();

        foreach (var block in analysis.BlockPlans)
        {
            targets.Add((block.Block, block.BlockAnnotation));
        }

        foreach (var site in analysis.MethodSites)
        {
            targets.Add((site.Invocation, site.Annotation));
        }

        foreach (var site in analysis.ReadSites)
        {
            targets.Add((site.Site, site.Annotation));
        }

        foreach (var site in analysis.WriteSites)
        {
            targets.Add((site.Assignment, site.Annotation));
        }

        foreach (var site in analysis.CompoundSites)
        {
            targets.Add((site.Statement, site.StatementAnnotation));
        }

        foreach (var seam in analysis.ConditionalSeams)
        {
            targets.Add((seam.Binding, seam.Annotation));
        }

        return targets;
    }

    /// <summary>Recompile the candidate tree and assert ET site OriginalDefinition/parameter identity.</summary>
    private List<InvocationExpressionSyntax> VerifyExpressionTreeIdentity(
        CompilationUnitSyntax newRoot,
        SyntaxTree originalTree,
        IReadOnlyList<ExpressionTreeSiteMarker> markers)
    {
        var failures = new List<InvocationExpressionSyntax>();
        if (markers.Count == 0)
        {
            return failures;
        }

        var parseOptions = originalTree.Options as CSharpParseOptions ?? new CSharpParseOptions(LanguageVersion.Latest);
        var candidateTree = CSharpSyntaxTree.Create(newRoot, parseOptions, originalTree.FilePath);
        var verifyCompilation = _compilation!.ReplaceSyntaxTree(originalTree, candidateTree);
        var verifyModel = verifyCompilation.GetSemanticModel(candidateTree);
        var candidateRoot = candidateTree.GetCompilationUnitRoot();

        foreach (var marker in markers)
        {
            // Nodes must be located in the CANDIDATE tree (Create re-roots the
            // given root; instances from newRoot are not attached to it).
            var node = candidateRoot.GetAnnotatedNodes(marker.Annotation)
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault();
            var expectedParameters = marker.Unreduced.OriginalDefinition.Parameters
                .Select(p => p.Type.ToDisplayString())
                .ToList();

            var isIdentical = node is not null
                && verifyModel.GetSymbolInfo(node).Symbol is IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary,
                } rebound
                && rebound.OriginalDefinition.ToDisplayString()
                    == marker.Unreduced.OriginalDefinition.ToDisplayString()
                && node.ArgumentList.Arguments.Count == marker.Unreduced.Parameters.Length
                && rebound.OriginalDefinition.Parameters
                    .Select(p => p.Type.ToDisplayString())
                    .SequenceEqual(expectedParameters, StringComparer.Ordinal);

            if (isIdentical
                && ForceExpressionTreeMismatch?.Invoke(node!, marker.Unreduced) != true)
            {
                continue;
            }

            failures.Add(marker.OriginalInvocation);
        }

        return failures;
    }

    private void FinalizeSiteKeys(
        SyntaxTree tree,
        FilePlan plan,
        IReadOnlyList<ExtensionConditionalPlan> liftableConditions,
        IReadOnlyList<ExtensionGroupPlan> rewritableGroups,
        HashSet<SyntaxNode> rollback)
    {
        var sites = _rewrittenSites.ToList();

        foreach (var binding in plan.Bindings)
        {
            if (rollback.Contains(binding.Invocation))
            {
                continue;
            }

            AddSite(sites, tree.FilePath, binding.Invocation.SpanStart, binding.Unreduced,
                1 + binding.Invocation.ArgumentList.Arguments.Count);
        }

        foreach (var condition in liftableConditions)
        {
            foreach (var segment in condition.Segments)
            {
                foreach (var call in segment.SpineCalls)
                {
                    AddSite(sites, tree.FilePath, call.Invocation.SpanStart, call.Unreduced,
                        call.Unreduced.Parameters.Length);
                }
            }
        }

        foreach (var group in rewritableGroups)
        {
            AddSite(sites, tree.FilePath, group.MemberAccess.SpanStart, group.Unreduced,
                group.Unreduced.Parameters.Length);
        }

        _rewrittenSites = sites;
    }

    private static void AddSite(
        List<ExtensionRewrittenSiteKey> sites,
        string filePath,
        int spanStart,
        IMethodSymbol unreduced,
        int argumentCount)
    {
        var definition = unreduced.OriginalDefinition;
        sites.Add(new ExtensionRewrittenSiteKey(
            filePath,
            spanStart,
            definition.ToDisplayString(),
            argumentCount,
            definition.Parameters.Select(p => p.Type.ToDisplayString()).ToList()));
    }

    private void FinalizeExemptSites(
        SyntaxTree tree,
        FilePlan plan,
        HashSet<SyntaxNode> rollback)
    {
        var exempts = _exemptSites.ToList();

        foreach (var site in plan.RefMismatchSites)
        {
            AddExempt(exempts, tree, site, ExtensionExemptKind.RefReceiverMismatch, site.ToString());
        }

        foreach (var site in plan.ExprTreeReportOnlySites)
        {
            AddExempt(exempts, tree, site, ExtensionExemptKind.ExprTreeRollback, site.ToString());
        }

        foreach (var site in plan.MixedChainSkippedSites)
        {
            AddExempt(exempts, tree, site, ExtensionExemptKind.MixedLinqChain, site.ToString());
        }

        foreach (var site in plan.OverrideSkippedSites)
        {
            AddExempt(exempts, tree, site, ExtensionExemptKind.HostOverrideSkip, site.ToString());
        }

        foreach (var invocation in rollback.Cast<InvocationExpressionSyntax>())
        {
            AddExempt(exempts, tree, invocation, ExtensionExemptKind.ExprTreeRollback, invocation.ToString());
        }

        foreach (var condition in plan.ConditionalPlans.Where(p => !p.Liftable))
        {
            var kind = condition.SkipCode == ExtensionCallBinder.NullConditionalInExpressionTreeReason
                ? ExtensionExemptKind.ExprTreeNullConditional
                : ExtensionExemptKind.NullConditionalUnliftable;
            exempts.Add(new ExtensionExemptSite(
                tree.FilePath,
                condition.OuterConditional.SpanStart,
                kind,
                ExtensionMethodResidualScanner.CompactSource(condition.OuterConditional.ToString()))
            {
                SpanLength = condition.OuterConditional.Span.Length,
            });
        }

        foreach (var group in plan.GroupPlans.Where(g => !g.Rewritable))
        {
            AddExempt(exempts, tree, group.MemberAccess, ExtensionExemptKind.ReducedMethodGroup,
                group.MemberAccess.ToString());
        }

        _exemptSites = exempts;
    }

    private static void AddExempt(
        List<ExtensionExemptSite> exempts,
        SyntaxTree tree,
        SyntaxNode node,
        ExtensionExemptKind kind,
        string text)
    {
        exempts.Add(new ExtensionExemptSite(
            tree.FilePath,
            node.SpanStart,
            kind,
            ExtensionMethodResidualScanner.CompactSource(text)));
    }

    private static int CountRewrittenInvocations(
        CompilationUnitSyntax root,
        FilePlan plan,
        IReadOnlyList<ExtensionConditionalPlan> liftableConditions,
        IReadOnlyList<ExtensionGroupPlan> rewritableGroups,
        HashSet<SyntaxNode> rollback)
        => plan.Bindings.Count(b => !rollback.Contains(b.Invocation))
           + liftableConditions.Sum(c => c.Segments.Sum(s => s.SpineCalls.Count))
           + rewritableGroups.Count;

    private static int CountGenericHosts(
        FilePlan plan,
        IReadOnlyList<ExtensionConditionalPlan> liftableConditions,
        IReadOnlyList<ExtensionGroupPlan> rewritableGroups,
        HashSet<SyntaxNode> rollback)
    {
        var count = plan.Bindings
            .Count(b => !rollback.Contains(b.Invocation) && b.Unreduced.ContainingType.IsGenericType);
        count += liftableConditions
            .SelectMany(c => c.Segments.SelectMany(s => s.SpineCalls))
            .Count(c => c.Unreduced.ContainingType.IsGenericType);
        count += rewritableGroups.Count(g => g.Unreduced.ContainingType.IsGenericType);
        return count;
    }

    /// <summary>Post-rewrite residual scan over the recompiled intermediate tree (gates 1/2/3/4/5/6).</summary>
    public Dictionary<string, int> ResidualScan(Compilation rewritten)
        => ResidualScan(rewritten, null);

    /// <summary>
    /// Post-rewrite residual scan; <paramref name="excludedFiles"/> are files
    /// reverted byte-for-byte by the M4 build-failure rollback (full absolute
    /// paths, ordinal-ignore compare) — their sugar is owned by the rollback,
    /// not by this pass.
    /// </summary>
    public Dictionary<string, int> ResidualScan(
        Compilation rewritten,
        IReadOnlyCollection<string>? excludedFiles)
        => ExtensionMethodResidualScanner.Scan(
            rewritten, _rewrittenSites, _options, _exemptSites,
            excludedFiles, _thisModifierBaseline);

    /// <summary>
    /// Three-level formatting: (1) NormalizeWhitespace on the outermost
    /// rewritten regions only; (2) whole-file NormalizeWhitespace when temps
    /// and new statements/lambdas were inserted (or region formatting fails);
    /// (3) raw tree text as the last resort.
    /// </summary>
    private static string Format(CompilationUnitSyntax root, bool wholeFile)
    {
        if (wholeFile)
        {
            try
            {
                return root.NormalizeWhitespace().ToFullString();
            }
            catch
            {
                return root.ToFullString();
            }
        }

        try
        {
            var rewritten = root.GetAnnotatedNodes(ExtensionCallRewriter.RewrittenAnnotation)
                .OfType<InvocationExpressionSyntax>()
                .ToList();
            var outermost = rewritten
                .Where(node => !rewritten.Any(other => other != node && node.Ancestors().Contains(other)))
                .ToList();

            if (outermost.Count > 0)
            {
                var formatted = outermost.ToDictionary(
                    node => (SyntaxNode)node,
                    node =>
                    {
                        var leading = node.GetLeadingTrivia();
                        return (SyntaxNode)node.NormalizeWhitespace().WithLeadingTrivia(leading);
                    });
                var regionRoot = root.ReplaceNodes(outermost, (original, _) => formatted[original]);
                return regionRoot.ToFullString();
            }
        }
        catch
        {
            // Fall through to whole-file formatting.
        }

        try
        {
            return root.NormalizeWhitespace().ToFullString();
        }
        catch
        {
            return root.ToFullString();
        }
    }
}
