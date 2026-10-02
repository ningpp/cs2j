using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// C23 interpolation-expansion rule (fixed family order: first).
///
/// Analyze walks every <see cref="InterpolatedStringExpressionSyntax"/> site:
/// expression-tree exemption (CS2J0515) first, then the IE-1 five-way handler
/// binding (hard gate G0), then hole classification for default-string sites.
/// Part A emits IE-FOLD (0503) and IE-CONCAT (0501); sites that require
/// IE-FORMAT take the explained-preservation bridge (0502) until the Part B
/// Format emitter lands. Strict Error diagnostics abort the whole file in the
/// family entry; non-strict/Keep degrade them to Warnings and preserve the
/// affected sites while siblings rewrite.
/// </summary>
internal sealed class InterpolationRule : IExpressionDesugarRule
{
    public string Id => "Interpolations";

    public bool IsPotentiallyDirty(
        CompilationUnitSyntax root,
        ExpressionDesugarOptions options)
        // Rule-level opt-in: the family is on by default for C36, so C23 must
        // stay completely inert (byte-identical output) until explicitly enabled.
        => options.Interpolations.Enabled
           && root.DescendantNodes()
               .OfType<InterpolatedStringExpressionSyntax>()
               .Any();

    public ExpressionDesugarRulePlan Analyze(RuleAnalysisContext context)
    {
        var plans =
            new Dictionary<InterpolatedStringExpressionSyntax, InterpolationPlan>();
        var model = context.SemanticModel;

        // Unreachable under the current family entry (it skips model-less
        // files); stay safe and produce an empty plan if it ever happens.
        if (model != null)
        {
            // DescendantNodes is pre-order, so an enclosing interpolation is
            // decided before its nested sites. Sites nested inside a PRESERVED
            // outer (handler/Format bridge/expression tree/...) must not be
            // rewritten on their own: they inherit the outer disposition
            // silently (no duplicate diagnostics/counters). Nested sites
            // inside Fold/Concat outers are analyzed normally because the
            // bottom-up rewriter independently expands them first.
            var preservedEnclosures = new HashSet<InterpolatedStringExpressionSyntax>();

            foreach (var node in context.Root.DescendantNodes()
                         .OfType<InterpolatedStringExpressionSyntax>())
            {
                var enclosing = node.Ancestors()
                    .OfType<InterpolatedStringExpressionSyntax>()
                    .FirstOrDefault(preservedEnclosures.Contains);

                var plan = enclosing != null
                    ? BuildEnclosedPlan(node, plans[enclosing])
                    : AnalyzeSite(context, model, node);

                plans[node] = plan;

                if (plan.Path is not (InterpolationPath.Fold
                        or InterpolationPath.Concat))
                    preservedEnclosures.Add(node);
            }
        }

        return new InterpolationRulePlan(context, plans);
    }

    /// <summary>
    /// Nested site inside an already-preserved outer: inherit the outer's
    /// disposition without diagnostics or bucket counts (the outer node's
    /// explained annotation covers the whole subtree in the residual gate).
    /// </summary>
    private static InterpolationPlan BuildEnclosedPlan(
        InterpolatedStringExpressionSyntax node,
        InterpolationPlan outer)
        => new()
        {
            Node = node,
            BindingKind = outer.BindingKind,
            Path = outer.Path,
            HandlerTypeName = outer.HandlerTypeName,
            Depth = node.Ancestors().OfType<InterpolatedStringExpressionSyntax>().Count(),
        };

    private static InterpolationPlan AnalyzeSite(
        RuleAnalysisContext context,
        SemanticModel model,
        InterpolatedStringExpressionSyntax node)
    {
        var stats = context.Statistics.Interpolations;
        var options = context.Options.Interpolations;
        stats.InterpolationSites++;

        var depth = node.Ancestors().OfType<InterpolatedStringExpressionSyntax>().Count();
        if (depth > 0)
            stats.NestedInterpolationSites++;

        var plan = new InterpolationPlan
        {
            Node = node,
            Depth = depth,
        };

        // IE-5 expression-tree exemption runs before binding (Q4: keep until
        // compiler-canonical lowering is proven in a later milestone).
        if (!options.RewriteInExpressionTrees
            && ExpressionDesugarSyntaxFacts.IsInExpressionTree(model, node))
        {
            plan.BindingKind = InterpolationBindingKind.DefaultString;
            plan.Path = InterpolationPath.SkippedExpressionTree;
            plan.DiagnosticCodes.Add(InterpolationDiagnosticCodes.CS2J0515);
            stats.ExpressionTreeSitesSkipped++;
            stats.AddSkip(InterpolationDiagnosticCodes.CS2J0515);
            context.Report(
                ExpressionDesugarSeverity.Info,
                InterpolationDiagnosticCodes.CS2J0515,
                InterpolationDiagnostics.ExpressionTreeMessage,
                node);
            return plan;
        }

        var binding = HandlerBindingAnalyzer.Classify(model, node);
        plan.BindingKind = binding.Kind;
        plan.HandlerTypeName = binding.HandlerTypeName;
        plan.ConstantValue = binding.ConstantValue;

        switch (binding.Kind)
        {
            case InterpolationBindingKind.Constant
                when options.FoldConstants:
            {
                plan.Path = InterpolationPath.Fold;
                plan.DiagnosticCodes.Add(InterpolationDiagnosticCodes.CS2J0503);
                stats.ConstantsFolded++;
                context.Report(
                    ExpressionDesugarSeverity.Info,
                    InterpolationDiagnosticCodes.CS2J0503,
                    InterpolationDiagnostics.FoldMessage,
                    node);
                return plan;
            }

            case InterpolationBindingKind.FormattableString:
            {
                plan.Path = InterpolationPath.KeptFormattableString;
                plan.DiagnosticCodes.Add(InterpolationDiagnosticCodes.CS2J0511);
                stats.KeptFormattableStringSites++;
                stats.AddSkip(InterpolationDiagnosticCodes.CS2J0511);
                context.Report(
                    ExpressionDesugarSeverity.Warning,
                    InterpolationDiagnosticCodes.CS2J0511,
                    InterpolationDiagnostics.FormattableStringMessage,
                    node);
                return plan;
            }

            case InterpolationBindingKind.CustomHandler:
            {
                plan.Path = InterpolationPath.CustomHandlerErrorOrKeep;
                plan.DiagnosticCodes.Add(InterpolationDiagnosticCodes.CS2J0510);
                stats.CustomHandlerSites++;
                stats.AddSkip(InterpolationDiagnosticCodes.CS2J0510);
                var severity = options.HandlerPolicy == HandlerUnsupportedPolicy.Keep
                    ? ExpressionDesugarSeverity.Warning
                    : InterpolationDiagnostics.ResolveSeverity(
                        InterpolationDiagnosticCodes.CS2J0510, options.Strict);
                context.Report(
                    severity,
                    InterpolationDiagnosticCodes.CS2J0510,
                    string.Format(
                        InterpolationDiagnostics.CustomHandlerMessage,
                        binding.HandlerTypeName ?? "?"),
                    node);
                return plan;
            }

            case InterpolationBindingKind.Unknown:
            {
                plan.Path = InterpolationPath.UnknownError;
                plan.DiagnosticCodes.Add(InterpolationDiagnosticCodes.CS2J0512);
                stats.AddSkip(InterpolationDiagnosticCodes.CS2J0512);
                context.Report(
                    InterpolationDiagnostics.ResolveSeverity(
                        InterpolationDiagnosticCodes.CS2J0512, options.Strict),
                    InterpolationDiagnosticCodes.CS2J0512,
                    InterpolationDiagnostics.UnknownBindingMessage,
                    node);
                return plan;
            }
        }

        // DefaultString (and Constant with folding disabled): analyze holes.
        BuildSegments(model, plan);

        var badHole = plan.Segments
            .OfType<InterpolationSegment.Hole>()
            .Select(s => s.Info)
            .FirstOrDefault(h =>
                h.IsPointer || (h.IsRefStructLike && !h.HasSafeRefStructToString));

        if (badHole != null)
        {
            plan.Path = InterpolationPath.RefStructOrPointerError;
            plan.DiagnosticCodes.Add(InterpolationDiagnosticCodes.CS2J0513);
            stats.RefStructOrPointerRejections++;
            stats.AddSkip(InterpolationDiagnosticCodes.CS2J0513);
            context.Report(
                InterpolationDiagnostics.ResolveSeverity(
                    InterpolationDiagnosticCodes.CS2J0513, options.Strict),
                InterpolationDiagnosticCodes.CS2J0513,
                string.Format(
                    InterpolationDiagnostics.RefStructOrPointerMessage,
                    badHole.Type?.ToDisplayString() ?? "?"),
                node);
            return plan;
        }

        var needsFormat = plan.Segments
            .OfType<InterpolationSegment.Hole>()
            .Any(h => h.Info.HasAlignmentClause
                      || h.Info.HasFormatClause
                      || h.Info.NeedsFormatUpgrade);

        if (needsFormat)
        {
            // Part A bridge: keep compilable, pair the residual with this
            // explanation. Part B emits real string.Format here.
            plan.Path = InterpolationPath.PreservedNeedsFormat;
            plan.DiagnosticCodes.Add(InterpolationDiagnosticCodes.CS2J0502);
            stats.FormatDeferredSites++;
            stats.AddSkip(InterpolationDiagnosticCodes.CS2J0502);
            context.Report(
                ExpressionDesugarSeverity.Info,
                InterpolationDiagnosticCodes.CS2J0502,
                InterpolationDiagnostics.FormatDeferredMessage,
                node);
            return plan;
        }

        plan.Path = InterpolationPath.Concat;
        plan.DiagnosticCodes.Add(InterpolationDiagnosticCodes.CS2J0501);
        stats.ConcatExpansions++;
        foreach (var hole in plan.Segments.OfType<InterpolationSegment.Hole>()
                     .Select(s => s.Info))
        {
            if (hole.IsUnsignedNumeric)
                stats.UnsignedHoleWraps++;
            if (hole.HasSafeRefStructToString)
                stats.RefStructToStringWraps++;
        }

        context.Report(
            ExpressionDesugarSeverity.Info,
            InterpolationDiagnosticCodes.CS2J0501,
            InterpolationDiagnostics.ConcatMessage,
            node);
        return plan;
    }

    /// <summary>Builds the ordered segment list and per-hole semantic facts.</summary>
    private static void BuildSegments(
        SemanticModel model,
        InterpolationPlan plan)
    {
        var compilation = model.Compilation;

        foreach (var content in plan.Node.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    plan.Segments.Add(
                        new InterpolationSegment.Text(
                            InterpolationSyntaxFacts.GetTextValue(text)));
                    break;

                case InterpolationSyntax hole:
                {
                    var typeInfo = model.GetTypeInfo(hole.Expression);
                    var type = typeInfo.ConvertedType ?? typeInfo.Type;
                    var isRefStruct = type is { IsRefLikeType: true };
                    var isPointer = InterpolationSyntaxFacts.IsPointerType(type);
                    var safeToString = isRefStruct
                        && InterpolationSyntaxFacts.HasOwnParameterlessToStringOverride(type);
                    var whitelist = InterpolationSyntaxFacts.IsWhitelistType(type);
                    var formattable =
                        InterpolationSyntaxFacts.ImplementsIFormattable(compilation, type);

                    var info = new HoleInfo
                    {
                        Syntax = hole,
                        Expression = hole.Expression,
                        Type = type,
                        ConstantAlignment =
                            InterpolationSyntaxFacts.GetConstantAlignment(model, hole),
                        FormatText = InterpolationSyntaxFacts.GetFormatText(hole),
                        HasAlignmentClause = InterpolationSyntaxFacts.HasAlignment(hole),
                        HasFormatClause = InterpolationSyntaxFacts.HasFormat(hole),
                        UnsignedKind = InterpolationSyntaxFacts.GetUnsignedKind(type),
                        IsRefStructLike = isRefStruct,
                        IsPointer = isPointer,
                        HasSafeRefStructToString = safeToString,
                        NeedsFormatUpgrade = !whitelist && formattable,
                        IsWhitelistConcatSafe =
                            whitelist
                            || (!isPointer && !isRefStruct && !formattable),
                    };
                    plan.Segments.Add(new InterpolationSegment.Hole(info));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Analyze product: runs the single bottom-up rewriter (no pending
    /// temps — C23 main path is zero-temp) and the IE-6 residual gate.
    /// </summary>
    private sealed class InterpolationRulePlan : ExpressionDesugarRulePlan
    {
        private readonly RuleAnalysisContext _context;
        private readonly IReadOnlyDictionary<InterpolatedStringExpressionSyntax, InterpolationPlan> _plans;
        private InterpolationRewriter? _rewriter;

        public InterpolationRulePlan(
            RuleAnalysisContext context,
            IReadOnlyDictionary<InterpolatedStringExpressionSyntax, InterpolationPlan> plans)
        {
            _context = context;
            _plans = plans;
        }

        public override CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
        {
            _rewriter = new InterpolationRewriter(_plans);
            return (CompilationUnitSyntax)_rewriter.Visit(root)!;
        }

        public override IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp> PendingTemps
            => Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

        public override IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
            => new Dictionary<SyntaxNode, SyntaxAnnotation>();

        public override int RewriteCount => _rewriter?.RewriteCount ?? 0;

        public override int CheckResiduals(
            RuleAnalysisContext context,
            CompilationUnitSyntax root)
        {
            var unpaired = 0;
            SyntaxNode? firstResidual = null;

            foreach (var node in root.DescendantNodes()
                         .OfType<InterpolatedStringExpressionSyntax>())
            {
                if (InterpolationSyntaxFacts.IsExplained(node))
                    continue;

                unpaired++;
                firstResidual ??= node;
            }

            var stats = context.Statistics.Interpolations;
            stats.ResidualInterpolationCount = unpaired;

            if (unpaired > 0)
            {
                var strict = context.Options.Interpolations.Strict;
                context.Report(
                    InterpolationDiagnostics.ResolveSeverity(
                        InterpolationDiagnosticCodes.CS2J0516, strict),
                    InterpolationDiagnosticCodes.CS2J0516,
                    string.Format(InterpolationDiagnostics.ResidualMessage, unpaired),
                    firstResidual);
            }

            return unpaired;
        }
    }
}
