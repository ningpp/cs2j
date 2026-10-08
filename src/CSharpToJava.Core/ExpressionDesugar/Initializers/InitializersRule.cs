using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 object/collection-initializer rule (fixed family order: second, after
/// Interpolations(C23) and before NullConditional(C35)/CollectionExpr(C36)).
/// Plans are semantic and keyed to the ORIGINAL analysis tree; because an
/// earlier in-family rewrite (C23) refreshes node identity for every
/// traversed sibling, plan sites/owners/arg-lists/anchors carry stable
/// <see cref="SyntaxAnnotation"/> markers stamped before Pass 2
/// (C23↔C24 coexistence; the C36 face keeps its whole-file deferral).
/// Rollout gate: inert unless the host provides
/// <see cref="ExpressionDesugarOptions.Initializers"/>.
/// </summary>
internal sealed class InitializersRule : IExpressionDesugarRule
{
    public string Id => "Initializers";

    public bool IsPotentiallyDirty(
        CompilationUnitSyntax root,
        ExpressionDesugarOptions options)
        // The M0 rollout gate lives in Analyze (Options.Initializers subsection):
        // trigger syntax stays "dirty" so the rule participates uniformly, but
        // without the subsection its plan rewrites nothing (byte-identical).
        => InitializerSyntaxFacts.HasTriggerSyntax(root);

    public ExpressionDesugarRulePlan Analyze(RuleAnalysisContext context)
    {
        // Rollout gate: the C24 subsection must be present (see class remarks).
        if (context.Options.Initializers is null)
            return new InitializersRulePlan(context, new InitializerAnalysis
            {
                Plans = Array.Empty<InitializerPlan>(),
                OwnerTemps = new Dictionary<SyntaxNode, string>(),
                HoistedArguments = new Dictionary<ArgumentSyntax, string>(),
            });

        return new InitializersRulePlan(context, InitializerBindingAnalyzer.Analyze(context));
    }

    /// <summary>
    /// Analyze product: owns the site plans and runs the bottom-up rewriter.
    /// Sites deferred for C36 coexistence (nested inside a collection
    /// expression) stay out of the rewriter map, so no ancestor is rebuilt and
    /// C36's original-node-keyed plans keep resolving. Residual checking is
    /// deferred to M3.</summary>
    private sealed class InitializersRulePlan : ExpressionDesugarRulePlan
    {
        private readonly RuleAnalysisContext _context;
        private readonly InitializerAnalysis _analysis;
        private readonly InitializerStableMarkers? _markers;
        private InitializerRewriter? _rewriter;

        public InitializersRulePlan(RuleAnalysisContext context, InitializerAnalysis analysis)
        {
            _context = context;
            _analysis = analysis;
            // Always built: the rewriter runs even when every plan is deferred
            // (it simply rewrites nothing); the markers stay empty then.
            _markers = InitializerStableMarkers.Build(analysis);
        }

        public override void ContributeStamps(
            Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps)
            => _markers?.ContributeTo(stamps);

        public override CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
        {
            _rewriter = new InitializerRewriter(
                _analysis, _context.SemanticModel!, _markers!);
            return (CompilationUnitSyntax)_rewriter.Visit(root)!;
        }

        public override IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp> PendingTemps
            => (IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp>?)_rewriter?.Pending
               ?? Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

        public override IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
            => _rewriter?.AnchorAnnotations
               ?? _markers?.AnchorAnnotations
               ?? new Dictionary<SyntaxNode, SyntaxAnnotation>();

        public override int RewriteCount => _rewriter?.RewriteCount ?? 0;

        public override int CheckResiduals(
            RuleAnalysisContext context, CompilationUnitSyntax root)
        {
            // Rollout gate: without the C24 subsection the rule is inert, so it
            // must not police residual initializer syntax (which in the default
            // chain may be C36's emitted zero-temp `new List<T>(){…}`).
            if (context.Options.Initializers is null)
                return 0;

            // Whole-file C36-coexistence deferral: C24 rewrote nothing, so the
            // only residual nodes are the deferred C24 sites (paired by count)
            // and C36-emitted initializer syntax (owned by C36's own CS2J0589
            // gate). Reporting here would be a false positive.
            if (_analysis.Plans.Any(
                    p => p.Outcome == InitializerOutcome.DeferredForCoexistence))
            {
                return 0;
            }

            var unpaired = 0;
            SyntaxNode? firstResidual = null;

            foreach (var node in root.DescendantNodes())
            {
                if (!InitializerSyntaxFacts.IsResidualNode(node))
                    continue;

                // Preserved sites (M2-shape/diagnostic skips and lowered
                // subtrees) carry the explained annotation; array/anonymous
                // nodes are excluded by IsResidualNode itself.
                if (InitializerSyntaxFacts.IsExplained(node))
                    continue;

                unpaired++;
                firstResidual ??= node;
            }

            var stats = context.Statistics.Initializers;
            stats.ResidualInitializerCount = unpaired;

            if (unpaired > 0)
            {
                var strict = (context.Options.Initializers ?? InitializerOptions.Default).Strict;
                context.Report(
                    InitializerDiagnostics.ResolveSeverity(
                        InitializerDiagnosticCodes.CS2J0545, strict),
                    InitializerDiagnosticCodes.CS2J0545,
                    string.Format(InitializerDiagnostics.ResidualMessage, unpaired),
                    firstResidual);
            }

            return unpaired;
        }
    }
}
