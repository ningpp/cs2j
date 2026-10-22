using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// C45 stage (design §4.1/§9, D8/D16): replaces every planned lambda node with
/// an explicit delegate creation — `new &lt;DelType&gt;(Cs2jLambda_&lt;member&gt;_&lt;k&gt;)` for
/// the M1 static path and `new &lt;DelType&gt;(__cs2jClosure&lt;n&gt;.Lambda_&lt;k&gt;)` for the
/// M2 instance path (multicast Combine/Remove arguments and event `+=` lambdas
/// are hit by the same bottom-up traversal without dedicated branches, D16).
///
/// Matching is ORDER-BASED (M2): the model tree's lambdas pair 1:1 with the
/// final tree's MAIN-TREE lambdas — lambdas hosted inside synthesized methods
/// (cs2j_closure_lift `method:` annotation) are excluded, because their nested
/// planned sites were substituted at build time. Order pairing survives the
/// C32/M1/M2 edit vocabulary even where absolute spans shift (statement
/// insertions), which raw span matching cannot. Quoted lambdas and annotated
/// nodes never enter the plans.
/// </summary>
internal sealed class DelegateSiteRewriteStage : IClosureLiftStage
{
    public string Id => "delegate-site-rewrite";

    public ClosureStageResult Apply(ClosureStageInput input)
    {
        var (root, model, options, naming, statistics, diagnostics) = input;

        if (input.PlanBag is not { } bag
            || (bag.StaticLambdas.Count == 0 && bag.InstanceLambdas.Count == 0))
        {
            return new ClosureStageResult(root, false);
        }

        var modelRoot = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var modelLambdas = modelRoot.DescendantNodes().OfType<LambdaExpressionSyntax>().ToList();
        var mainTreeLambdas = root.DescendantNodes()
            .OfType<LambdaExpressionSyntax>()
            .Where(static l => !InsideSynthesizedMethod(l))
            .ToList();

        // A count mismatch means an unexpected edit vocabulary: skip site
        // rewriting for the file instead of guessing (fail-clean).
        if (modelLambdas.Count != mainTreeLambdas.Count)
            return new ClosureStageResult(root, false);

        var staticPlans = bag.StaticLambdas.ToDictionary(
            static p => (p.SiteSpanStart, p.SiteRawKind),
            static p => p);
        var instancePlans = bag.InstanceLambdas.ToDictionary(
            static p => (p.SiteSpanStart, p.SiteRawKind),
            static p => p);

        var plansByFinalNode = new Dictionary<LambdaExpressionSyntax, (StaticLambdaPlan? Static, DelegateSitePlan? Instance, bool Bare)>();
        for (var i = 0; i < modelLambdas.Count; i++)
        {
            var key = (modelLambdas[i].SpanStart, modelLambdas[i].RawKind);
            // `new D(λ)` already performs the delegate conversion: emitting
            // `new D(method)` inside it would double-wrap and produce
            // `new D(new D(method))`. Such sites get the bare method group
            // (the outer creation keeps the delegate type).
            var bare = IsDirectDelegateCreationArgument(modelLambdas[i], model);
            if (staticPlans.TryGetValue(key, out var staticPlan))
                plansByFinalNode[mainTreeLambdas[i]] = (staticPlan, null, bare);
            else if (instancePlans.TryGetValue(key, out var instancePlan))
                plansByFinalNode[mainTreeLambdas[i]] = (null, instancePlan, bare);
        }

        var rewriter = new SiteRewriter(plansByFinalNode, statistics);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;

        return new ClosureStageResult(rewritten, rewriter.SitesRewritten > 0);
    }

    private static bool InsideSynthesizedMethod(LambdaExpressionSyntax lambda)
        => lambda.Ancestors().Any(static a => a.GetAnnotations(ClosureLiftAnnotations.Kind)
            .Any(static x => x.Data is { } data && data.StartsWith("method:", StringComparison.Ordinal)));

    /// <summary>
    /// True when the lambda is the sole argument of a delegate-creation
    /// expression (<c>new D(λ)</c>): the outer creation already supplies the
    /// delegate type, so its site must be replaced by the bare method group.
    /// </summary>
    private static bool IsDirectDelegateCreationArgument(
        LambdaExpressionSyntax lambda, SemanticModel model)
    {
        if (lambda.Parent is not ArgumentSyntax argument
            || argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Arguments.Count != 1
            || argumentList.Parent is not ObjectCreationExpressionSyntax creation)
        {
            return false;
        }

        return model.GetTypeInfo(creation).Type?.TypeKind == TypeKind.Delegate;
    }

    private sealed class SiteRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<LambdaExpressionSyntax, (StaticLambdaPlan? Static, DelegateSitePlan? Instance, bool Bare)> _plansByNode;
        private readonly ClosureLiftStatistics? _statistics;

        public SiteRewriter(
            Dictionary<LambdaExpressionSyntax, (StaticLambdaPlan? Static, DelegateSitePlan? Instance, bool Bare)> plansByNode,
            ClosureLiftStatistics? statistics)
        {
            _plansByNode = plansByNode;
            _statistics = statistics;
        }

        public int SitesRewritten { get; private set; }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            => ReplaceOrVisit(node);

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            => ReplaceOrVisit(node);

        private SyntaxNode? ReplaceOrVisit(LambdaExpressionSyntax node)
        {
            // Lookup BEFORE descending: a matched site is replaced as a whole,
            // so its body (with any nested sites) is never double-counted.
            if (_plansByNode.TryGetValue(node, out var plan))
            {
                SitesRewritten += 1;
                if (_statistics != null)
                    _statistics.DelegateSitesRewritten += 1;

                if (plan.Static is { } staticPlan)
                {
                    var staticNode = plan.Bare
                        ? SyntaxFactory.ParseExpression(staticPlan.MethodName)
                        : StaticLambdaHandler.BuildSiteReplacement(staticPlan);
                    return staticNode
                        .WithTriviaFrom(node)
                        .WithAdditionalAnnotations(ClosureLiftAnnotations.LiftedSite(staticPlan.MethodName));
                }

                var instancePlan = plan.Instance!;
                var receiver = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(instancePlan.ReceiverExpression),
                    SyntaxFactory.IdentifierName(instancePlan.MethodName));

                if (plan.Bare)
                {
                    return receiver
                        .WithTriviaFrom(node)
                        .WithAdditionalAnnotations(ClosureLiftAnnotations.LiftedSite(instancePlan.MethodName));
                }

                return SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName(instancePlan.DelegateTypeDisplay))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(receiver))))
                    .WithTriviaFrom(node)
                    .WithAdditionalAnnotations(ClosureLiftAnnotations.LiftedSite(instancePlan.MethodName));
            }

            // The SPECIFIC base visit: base.Visit(node) would re-dispatch into
            // this override and loop forever.
            return node switch
            {
                SimpleLambdaExpressionSyntax simple => base.VisitSimpleLambdaExpression(simple),
                ParenthesizedLambdaExpressionSyntax parenthesized
                    => base.VisitParenthesizedLambdaExpression(parenthesized),
                _ => base.Visit(node),
            };
        }
    }
}
