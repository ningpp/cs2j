using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// C36 collection-expression rule (fixed family order: last).
///
/// Analyze walks every <see cref="CollectionExpressionSyntax"/> site, builds a
/// <see cref="CollectionExprPlan"/> (target classification via
/// <see cref="TargetTypeResolver"/>, exhaustive element plans, spread source
/// classification, liftability) and emits the skip diagnostics
/// (0571/0576/0580/0581/0583). Strict Error diagnostics abort the whole file in
/// the family entry; non-strict downgrades them to Warnings and the affected
/// sites are preserved while siblings rewrite. Rewrite is one bottom-up
/// <see cref="CollectionExprRewriter"/>; CE-12 pairs every residual with a skip
/// in <see cref="CollectionExprRulePlan.CheckResiduals"/>.
/// </summary>
internal sealed class CollectionExprRule : IExpressionDesugarRule
{
    public string Id => "CollectionExpr";

    public bool IsPotentiallyDirty(
        CompilationUnitSyntax root,
        ExpressionDesugarOptions options)
        => root.DescendantNodes().Any(static node => node is CollectionExpressionSyntax)
           || NewSyntaxGuardAnalyzer.ContainsGuardSyntax(root);

    public ExpressionDesugarRulePlan Analyze(RuleAnalysisContext context)
    {
        var plans = new Dictionary<CollectionExpressionSyntax, CollectionExprPlan>();
        var model = context.SemanticModel;

        // Syntax-shape counters are safe without a model; semantic plans need one.
        foreach (var node in context.Root.DescendantNodes()
                     .OfType<CollectionExpressionSyntax>())
        {
            var plan = model != null
                ? AnalyzeSite(context, model, node)
                : BuildShapeOnlyPlan(node);
            plans[node] = plan;
        }

        // CE-GUARD (M3): semantic inventory 0585/0586/0587 runs in the SAME
        // Analyze pass; 0588 is defended per collection site below. Guard
        // Errors flow into the family's ordinary atomic gate (a guard Error
        // makes the whole file Changed=false even when sibling sites rewrite).
        if (context.Options.CollectionExpr.EnableGuard)
            NewSyntaxGuardAnalyzer.Analyze(context);

        // CE-8 (M2): deterministic file-wide pre-allocation of temp names,
        // foreach variables and lift orders for every statement-sequence site
        // (post-order: inner sites before enclosing sites; siblings in source
        // order). The rewriter consumes the pre-allocated values instead of
        // numbering during its bottom-up visit.
        if (model != null)
            AllocateStatementSequenceNames(context, plans.Values);

        return new CollectionExprRulePlan(context, plans);
    }

    private static CollectionExprPlan BuildShapeOnlyPlan(CollectionExpressionSyntax node)
        => new()
        {
            Node = node,
            TargetKind = TargetKind.Unknown,
            Outcome = CollectionExprOutcome.SkippedDiagnostic,
            Depth = node.Ancestors().OfType<CollectionExpressionSyntax>().Count(),
        };

    private static CollectionExprPlan AnalyzeSite(
        RuleAnalysisContext context,
        SemanticModel model,
        CollectionExpressionSyntax node)
    {
        var stats = context.Statistics.CollectionExpr;
        stats.CollectionSites++;

        var isEmpty = node.Elements.Count == 0;
        if (isEmpty)
            stats.EmptySites++;
        var depth = node.Ancestors().OfType<CollectionExpressionSyntax>().Count();
        if (depth > 0)
            stats.NestedSites++;
        var containsSpread = node.Elements
            .Any(CollectionExprSyntaxFacts.IsSpread);

        // CE-GUARD 0588 (defensive; baseline CS9175 makes this unreachable in
        // compilable input): a collection expression inside an expression tree
        // is never lowered. Paired skip so the per-file and whole-tree
        // residual gates see one explained site (+ its spreads).
        if (ExpressionDesugarSyntaxFacts.IsInExpressionTree(model, node))
        {
            var preservedSpreads = node.Elements
                .Count(CollectionExprSyntaxFacts.IsSpread);
            stats.SpreadElements += preservedSpreads;
            stats.SkippedSpreadElements += preservedSpreads;

            context.Report(
                ExpressionDesugarSeverity.Info,
                CollectionExprDiagnosticCodes.CS2J0588,
                CollectionExprDiagnostics.ExpressionTreeMessage,
                node);
            stats.AddSkip(CollectionExprDiagnosticCodes.CS2J0588);

            return new CollectionExprPlan
            {
                Node = node,
                TargetKind = TargetKind.Unknown,
                Outcome = CollectionExprOutcome.SkippedDiagnostic,
                Depth = depth,
                ContainsSpread = containsSpread,
            };
        }

        // ---- Elements (exhaustive; OfType filtering is forbidden) ----
        var collectionOperation = model.GetOperation(node) as ICollectionExpressionOperation;
        var elements = new List<ElementPlan>(node.Elements.Count);
        var elementSkipCode = (string?)null;
        var elementSkipMessage = (string?)null;

        for (var i = 0; i < node.Elements.Count; i++)
        {
            var syntax = node.Elements[i];
            var elementKind = CollectionExprSyntaxFacts.ClassifyElement(syntax);
            var boundOperation = collectionOperation is { Elements.Length: var n } && n == node.Elements.Count
                ? collectionOperation.Elements[i]
                : null;

            switch (syntax)
            {
                case ExpressionElementSyntax expressionElement:
                {
                    ITypeSymbol? fedType = boundOperation?.Type;
                    fedType ??= model.GetTypeInfo(expressionElement.Expression).ConvertedType;
                    fedType ??= model.GetTypeInfo(expressionElement.Expression).Type;
                    elements.Add(new ElementPlan
                    {
                        Syntax = syntax,
                        Kind = CollectionElementKind.Expression,
                        Expression = expressionElement.Expression,
                        FedType = fedType,
                    });
                    break;
                }

                case SpreadElementSyntax spreadElement:
                {
                    stats.SpreadElements++;
                    var sourceOperation = (boundOperation as ISpreadOperation)?.Operand;
                    var sourceInfo = model.GetTypeInfo(spreadElement.Expression);
                    var sourceType = sourceOperation?.Type
                                     ?? sourceInfo.ConvertedType
                                     ?? sourceInfo.Type;
                    var sourceKind = SpreadExpander.ClassifySource(sourceType);
                    var elementType = SpreadExpander.TryGetElementType(
                        sourceType, model.Compilation);

                    elements.Add(new ElementPlan
                    {
                        Syntax = syntax,
                        Kind = CollectionElementKind.Spread,
                        Expression = spreadElement.Expression,
                        FedType = elementType,
                        Spread = new SpreadPlan
                        {
                            Syntax = spreadElement,
                            Source = spreadElement.Expression,
                            SourceKind = sourceKind,
                            ElementType = elementType,
                        },
                    });

                    if (sourceKind == SpreadSourceKind.Unknown)
                    {
                        elementSkipCode ??= CollectionExprDiagnosticCodes.CS2J0576;
                        elementSkipMessage ??= CollectionExprDiagnostics.SpreadNotEnumerableMessage;
                    }
                    break;
                }

                default:
                {
                    // Future/unknown element kind: explicit error, never a
                    // silent drop (hard invariant #3).
                    elementSkipCode ??= CollectionExprDiagnosticCodes.CS2J0581;
                    elementSkipMessage ??=
                        "Collection element syntax kind '" + syntax.Kind() +
                        "' is not supported by expression-desugar; the site " +
                        "was left unchanged.";
                    elements.Add(new ElementPlan
                    {
                        Syntax = syntax,
                        Kind = CollectionElementKind.Unsupported,
                    });
                    break;
                }
            }
        }

        // ---- Target classification (ConvertedType is the only truth) ----
        var fedTypes = elements.Select(e => e.FedType).ToList();
        var resolution = TargetTypeResolver.Resolve(model, node, fedTypes);
        stats.AddTargetKind(resolution.Kind);
        if (resolution.Kind == TargetKind.Span)
            stats.SpanArraySites++;
        if (resolution.Kind == TargetKind.CollectionBuilder)
            stats.BuilderSites++;

        var skipCode = (string?)null;
        var skipMessage = (string?)null;

        // Multidimensional arrays beat the missing/error defense when the rank
        // is still visible on the natural or converted type.
        var visibleRank = resolution.ArrayRank > 0
            ? resolution.ArrayRank
            : TargetTypeResolver.TryGetArrayRank(model, node);

        if (resolution.IsMissingOrError)
        {
            if (visibleRank > 1)
            {
                skipCode = CollectionExprDiagnosticCodes.CS2J0581;
                skipMessage = string.Format(
                    CollectionExprDiagnostics.TableOutMessage,
                    "rank-" + visibleRank + " array");
            }
            else
            {
                skipCode = CollectionExprDiagnosticCodes.CS2J0571;
                skipMessage = CollectionExprDiagnostics.MissingTargetMessage;
            }
        }
        else if (resolution.ArrayRank > 1)
        {
            skipCode = CollectionExprDiagnosticCodes.CS2J0581;
            skipMessage = string.Format(
                CollectionExprDiagnostics.TableOutMessage,
                "rank-" + resolution.ArrayRank + " array");
        }
        else
        {
            switch (resolution.Kind)
            {
                // CE-10: a uniquely resolved factory is emitted; every other
                // builder situation (missing/ambiguous/open generic) stays on
                // 0580 with the concrete symbol-resolution reason.
                case TargetKind.CollectionBuilder
                    when resolution.BuilderFactory == null:
                    skipCode = CollectionExprDiagnosticCodes.CS2J0580;
                    skipMessage = string.Format(
                        CollectionExprDiagnostics.BuilderUnresolvedMessage,
                        resolution.BuilderType?.ToDisplayString() ?? "?",
                        resolution.BuilderFactoryMethod ?? "?",
                        resolution.BuilderResolveError ?? "unknown resolution failure");
                    break;

                // CE-4: the five constructible interfaces map to List<T>;
                // every other interface is baseline CS9174 and unreachable
                // from compilable input (defensive skip, no invented type).
                case TargetKind.Interface when resolution.ConstructedType == null:
                    skipCode = CollectionExprDiagnosticCodes.CS2J0581;
                    skipMessage = string.Format(
                        CollectionExprDiagnostics.TableOutBaselineMessage,
                        resolution.ConvertedType!.ToDisplayString(),
                        resolution.BaselineNote ?? TargetTypeResolver.InterfaceBaselineNote);
                    break;

                case TargetKind.Unknown:
                    skipCode = CollectionExprDiagnosticCodes.CS2J0581;
                    skipMessage = string.Format(
                        CollectionExprDiagnostics.TableOutMessage,
                        resolution.ConvertedType!.ToDisplayString());
                    break;
            }
        }

        // CE-9: ref struct elements can never reach a heap array (baseline
        // CS9244/CS0611; defensive 0582 locked by a custom ref struct fixture).
        if (skipCode == null
            && resolution.Kind == TargetKind.Span
            && TryFindRefStructElement(resolution.TargetElementType, elements) is { } refStructElement)
        {
            skipCode = CollectionExprDiagnosticCodes.CS2J0582;
            skipMessage = string.Format(
                CollectionExprDiagnostics.RefStructElementMessage,
                refStructElement.ToDisplayString());
        }

        // Element-level problems (0576 non-enumerable spread / unsupported kind).
        skipCode ??= elementSkipCode;
        skipMessage ??= elementSkipMessage;

        var liftable = false;
        if (skipCode == null && containsSpread)
        {
            liftable = CollectionContextLowering.CanLowerAt(node, out var reason);
            if (!liftable)
            {
                skipCode = CollectionExprDiagnosticCodes.CS2J0583;
                skipMessage = reason == CollectionContextLowering.ConditionalBranchReason
                    ? CollectionExprDiagnostics.NotLiftableConditionalMessage
                    : string.Format(
                        CollectionExprDiagnostics.NotLiftableMessage, reason);
            }
        }

        var plan = new CollectionExprPlan
        {
            Node = node,
            TargetKind = resolution.Kind,
            ConvertedType = resolution.ConvertedType,
            BuilderType = resolution.BuilderType,
            BuilderFactoryMethod = resolution.BuilderFactoryMethod,
            BuilderFactory = resolution.BuilderFactory,
            ConstructedType = resolution.ConstructedType,
            TargetElementType = resolution.TargetElementType,
            AddMethod = resolution.AddMethod,
            Elements = elements,
            Depth = depth,
            ContainsSpread = containsSpread,
            Liftable = liftable,
        };

        if (skipCode != null)
        {
            plan.Outcome = CollectionExprOutcome.SkippedDiagnostic;
            var strict = context.Options.CollectionExpr.Strict;
            context.Report(
                CollectionExprDiagnostics.ResolveSeverity(skipCode, strict),
                skipCode, skipMessage!, node);
            stats.AddSkip(skipCode);

            // M3 whole-tree gate pairing: the preserved site leaves 1 node
            // (paired by the skip count) plus one residual node per spread it
            // carries (paired here). SpreadElement stats were already counted
            // during element analysis.
            stats.SkippedSpreadElements += node.Elements
                .Count(CollectionExprSyntaxFacts.IsSpread);
            return plan;
        }

        // M2 success buckets (counted before shape dispatch, so both zero-temp
        // and statement-sequence forms land in the same target bucket).
        if (resolution.Kind == TargetKind.Interface)
            stats.InterfaceConstructSites++;
        if (resolution.Kind == TargetKind.CollectionBuilder)
            stats.BuilderFactoryEmitted++;

        // CE-9: every Span site actually rewritten to a heap array gets the
        // stack->heap Info (C06 owns the true stack form).
        if (resolution.Kind == TargetKind.Span)
        {
            context.Report(
                ExpressionDesugarSeverity.Info,
                CollectionExprDiagnosticCodes.CS2J0578,
                CollectionExprDiagnostics.SpanArrayMessage,
                node);
        }

        if (containsSpread)
        {
            plan.Outcome = CollectionExprOutcome.StatementSequence;
            if (resolution.Kind == TargetKind.MutableConcrete)
                stats.MutableConstructSites++;

            // Decide AddRange vs foreach-Add at Analyze time (same symbol rule
            // the M1 emitter applied) so loop variables can be pre-allocated.
            DecideSpreadShapes(model, plan);
        }
        else
        {
            switch (resolution.Kind)
            {
                case TargetKind.MutableConcrete:
                    plan.Outcome = CollectionExprOutcome.ZeroTempMutableCollection;
                    stats.MutableConstructSites++;
                    break;

                // CE-4: interface target -> new List<T>(){...} / new List<T>().
                case TargetKind.Interface:
                    plan.Outcome = CollectionExprOutcome.ZeroTempMutableCollection;
                    break;

                // CE-10: global::Builder.Create<...>(new TElem[]{...});
                // empty site -> Create(new TElem[]{}).
                case TargetKind.CollectionBuilder:
                    plan.Outcome = CollectionExprOutcome.ZeroTempBuilderFactory;
                    break;

                default:
                    // Array and Span targets both emit the array form; T[] has
                    // an implicit conversion to Span<T> (0578 Info above).
                    plan.Outcome = CollectionExprOutcome.ZeroTempArray;
                    break;
            }
        }

        return plan;
    }

    /// <summary>
    /// Returns the first ref-struct type carried by a Span site: the target
    /// element T itself or any fed element (ordinary fed type or a spread
    /// source element type).
    /// </summary>
    private static ITypeSymbol? TryFindRefStructElement(
        ITypeSymbol? targetElementType,
        IReadOnlyList<ElementPlan> elements)
    {
        if (targetElementType is { IsRefLikeType: true })
            return targetElementType;

        foreach (var element in elements)
        {
            if (element.FedType is { IsRefLikeType: true } refLike)
                return refLike;
        }

        return null;
    }

    /// <summary>
    /// Sets <see cref="SpreadPlan.UseAddRange"/> for every spread of a
    /// statement-sequence plan using the same symbol rule as M1: List&lt;T&gt;
    /// temps (array/Span/interface/builder targets) and T3 concrete temps use
    /// AddRange only when an accessible compatible single-parameter AddRange
    /// exists; Span/string sources always lower to foreach-Add.
    /// </summary>
    private static void DecideSpreadShapes(SemanticModel model, CollectionExprPlan plan)
    {
        INamedTypeSymbol? receiverType;
        if (plan.UsesListTemp)
        {
            if (plan.TargetElementType == null)
                return;
            var listDefinition = model.Compilation
                .GetTypeByMetadataName("System.Collections.Generic.List`1");
            if (listDefinition == null)
                return;
            receiverType = listDefinition.Construct(plan.TargetElementType);
        }
        else
        {
            receiverType = plan.ConvertedType as INamedTypeSymbol;
        }

        if (receiverType == null)
            return;

        foreach (var element in plan.Elements)
        {
            if (element is not { Kind: CollectionElementKind.Spread, Spread: { } spread })
                continue;

            if (spread.SourceKind is not (SpreadSourceKind.Array or SpreadSourceKind.Enumerable))
                continue;

            var sourceInfo = model.GetTypeInfo(spread.Syntax.Expression);
            var sourceType = sourceInfo.ConvertedType ?? sourceInfo.Type;
            if (sourceType != null
                && SpreadExpander.HasCompatibleAddRange(model, receiverType, sourceType))
            {
                spread.UseAddRange = true;
            }
        }
    }

    /// <summary>
    /// CE-8 deterministic pre-allocation. Statement-sequence sites are visited
    /// in post-order (an enclosing site is ordered AFTER every site it
    /// contains; disjoint sites follow source order) so inner temps are
    /// declared before outer sequences reference them. Each site receives its
    /// <c>__ed_n</c> temp, its lift order, and one foreach variable per spread
    /// that does not lower to AddRange — all from one file-wide numbering space
    /// seeded from existing local names.
    /// </summary>
    private static void AllocateStatementSequenceNames(
        RuleAnalysisContext context,
        IEnumerable<CollectionExprPlan> allPlans)
    {
        var sequencePlans = allPlans
            .Where(p => p.Outcome == CollectionExprOutcome.StatementSequence)
            .ToList();
        if (sequencePlans.Count == 0)
            return;

        sequencePlans.Sort(ComparePostOrder);

        var allocator = new ExpressionDesugarTempNameAllocator(context.Root);

        var order = 0;
        foreach (var plan in sequencePlans)
        {
            plan.TempName = allocator.Allocate();
            plan.LiftOrder = order++;

            foreach (var element in plan.Elements)
            {
                if (element is { Kind: CollectionElementKind.Spread, Spread: { } spread }
                    && !spread.UseAddRange)
                {
                    spread.LoopVariable = allocator.Allocate();
                }
            }
        }
    }

    /// <summary>
    /// Post-order comparison: the contained of two nested sites sorts first;
    /// disjoint sites sort by source position (SpanStart). Ties keep the stable
    /// sort order.
    /// </summary>
    private static int ComparePostOrder(CollectionExprPlan left, CollectionExprPlan right)
    {
        if (ReferenceEquals(left, right))
            return 0;

        var leftNode = left.Node;
        var rightNode = right.Node;

        if (leftNode.Span.Contains(rightNode.Span))
            return 1;  // right is inside left → right (inner) first
        if (rightNode.Span.Contains(leftNode.Span))
            return -1; // left is inside right → left (inner) first

        return leftNode.SpanStart.CompareTo(rightNode.SpanStart);
    }

    /// <summary>
    /// Analyze product: owns the site plans and runs the bottom-up rewriter
    /// plus the CE-12 residual gate.
    /// </summary>
    private sealed class CollectionExprRulePlan : ExpressionDesugarRulePlan
    {
        private const string MarkerKind = "ed-coll-site";

        private readonly RuleAnalysisContext _context;
        private readonly IReadOnlyDictionary<CollectionExpressionSyntax, CollectionExprPlan> _plans;
        private readonly RuleSiteMarkers<CollectionExprPlan> _markers = new(MarkerKind);
        private readonly Dictionary<SyntaxNode, SyntaxAnnotation> _anchorAnnotations = new();
        private CollectionExprRewriter? _rewriter;

        public CollectionExprRulePlan(
            RuleAnalysisContext context,
            IReadOnlyDictionary<CollectionExpressionSyntax, CollectionExprPlan> plans)
        {
            _context = context;
            _plans = plans;
            // C36 runs last: earlier rules refresh node instances, so sites
            // and statement-lift anchors resolve through stable markers.
            foreach (var plan in plans.Values)
            {
                _markers.Add(plan.Node, plan);

                if (plan.Outcome == CollectionExprOutcome.StatementSequence
                    && ExpressionDesugarTempLifter.FindAnchor(plan.Node) is { } anchor)
                {
                    _anchorAnnotations[anchor] = new SyntaxAnnotation(
                        ExpressionDesugarTempLifter.AnchorAnnotationKind);
                }
            }
        }

        public override void ContributeStamps(
            Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps)
        {
            _markers.ContributeTo(stamps);
            foreach (var pair in _anchorAnnotations)
            {
                if (!stamps.TryGetValue(pair.Key, out var list))
                {
                    list = new List<SyntaxAnnotation>();
                    stamps[pair.Key] = list;
                }

                list.Add(pair.Value);
            }
        }

        public override CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
        {
            _rewriter = new CollectionExprRewriter(
                _context, _plans, _markers, _anchorAnnotations);
            return (CompilationUnitSyntax)_rewriter.Visit(root)!;
        }

        public override IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp> PendingTemps
            => (IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp>?)_rewriter?.Pending
               ?? Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

        public override IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
            => (IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation>?)_rewriter?.AnchorAnnotations
               ?? _anchorAnnotations;

        public override int RewriteCount => _rewriter?.RewriteCount ?? 0;

        public override int CheckResiduals(
            RuleAnalysisContext context, CompilationUnitSyntax root)
        {
            var unpaired = 0;
            SyntaxNode? firstResidual = null;

            foreach (var node in root.DescendantNodes())
            {
                if (!CollectionExprSyntaxFacts.IsResidualNode(node))
                    continue;

                // Paired when the node itself (or an enclosing preserved site)
                // carries the explained annotation.
                if (CollectionExprSyntaxFacts.IsExplained(node))
                    continue;

                unpaired++;
                firstResidual ??= node;
            }

            var stats = context.Statistics.CollectionExpr;
            stats.ResidualCollectionCount = unpaired;

            if (unpaired > 0)
            {
                var strict = context.Options.CollectionExpr.Strict;
                context.Report(
                    CollectionExprDiagnostics.ResolveSeverity(
                        CollectionExprDiagnosticCodes.CS2J0589, strict),
                    CollectionExprDiagnosticCodes.CS2J0589,
                    string.Format(CollectionExprDiagnostics.ResidualMessage, unpaired),
                    firstResidual);
            }

            return unpaired;
        }
    }
}
