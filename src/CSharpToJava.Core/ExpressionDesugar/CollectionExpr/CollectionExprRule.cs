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

    public bool IsPotentiallyDirty(CompilationUnitSyntax root)
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

        // CE-GUARD semantic inventory (0584-0588) lands in M3; the syntax-only
        // dirty scan in NewSyntaxGuardAnalyzer already participates.
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
                case TargetKind.CollectionBuilder:
                    skipCode = CollectionExprDiagnosticCodes.CS2J0580;
                    skipMessage = string.Format(
                        CollectionExprDiagnostics.BuilderV1Message,
                        resolution.BuilderType?.ToDisplayString() ?? "?",
                        resolution.BuilderFactoryMethod ?? "?");
                    break;

                case TargetKind.Interface:
                    // M1: every interface target is skipped (M2 owns the
                    // specification-concrete-type table). No List.of fallback.
                    skipCode = CollectionExprDiagnosticCodes.CS2J0581;
                    skipMessage = string.Format(
                        CollectionExprDiagnostics.TableOutMessage,
                        resolution.ConvertedType!.ToDisplayString());
                    break;

                case TargetKind.Unknown:
                    skipCode = CollectionExprDiagnosticCodes.CS2J0581;
                    skipMessage = string.Format(
                        CollectionExprDiagnostics.TableOutMessage,
                        resolution.ConvertedType!.ToDisplayString());
                    break;
            }
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
                skipMessage = string.Format(
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
            return plan;
        }

        if (containsSpread)
        {
            plan.Outcome = CollectionExprOutcome.StatementSequence;
            if (resolution.Kind == TargetKind.MutableConcrete)
                stats.MutableConstructSites++;
        }
        else if (resolution.Kind == TargetKind.MutableConcrete)
        {
            plan.Outcome = CollectionExprOutcome.ZeroTempMutableCollection;
            stats.MutableConstructSites++;
        }
        else
        {
            // Array and Span targets both emit the array form in M1 (T[] has an
            // implicit conversion to Span<T>; 0578/0582 land in M2).
            plan.Outcome = CollectionExprOutcome.ZeroTempArray;
        }

        return plan;
    }

    /// <summary>
    /// Analyze product: owns the site plans and runs the bottom-up rewriter
    /// plus the CE-12 residual gate.
    /// </summary>
    private sealed class CollectionExprRulePlan : ExpressionDesugarRulePlan
    {
        private readonly RuleAnalysisContext _context;
        private readonly IReadOnlyDictionary<CollectionExpressionSyntax, CollectionExprPlan> _plans;
        private CollectionExprRewriter? _rewriter;

        public CollectionExprRulePlan(
            RuleAnalysisContext context,
            IReadOnlyDictionary<CollectionExpressionSyntax, CollectionExprPlan> plans)
        {
            _context = context;
            _plans = plans;
        }

        public override CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
        {
            _rewriter = new CollectionExprRewriter(_context, _plans);
            return (CompilationUnitSyntax)_rewriter.Visit(root)!;
        }

        public override IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp> PendingTemps
            => (IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp>?)_rewriter?.Pending
               ?? Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

        public override IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
            => _rewriter?.AnchorAnnotations
               ?? new Dictionary<SyntaxNode, SyntaxAnnotation>();

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
