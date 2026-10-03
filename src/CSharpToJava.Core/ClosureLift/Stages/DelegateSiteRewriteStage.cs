using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// C45 M1 stage (design §4.1/§9, D8/D16): replaces every planned lambda node
/// with an explicit delegate creation `new &lt;DelType&gt;(Cs2jLambda_&lt;member&gt;_&lt;k&gt;)`.
/// Sites are matched by the plan's SpanStart + raw kind anchor: the member
/// spine append of <see cref="NamedMethodLiftStage"/> never shifts positions,
/// so every occurrence of a planned lambda — the original site and any copy
/// of its body relocated into the emitted named method (nested lambda case) —
/// carries the planned SpanStart and is rewritten in one traversal. Quoted
/// lambdas and annotated nodes never enter the plans (LambdaSiteIndex
/// excluded them).
/// </summary>
internal sealed class DelegateSiteRewriteStage : IClosureLiftStage
{
    public string Id => "delegate-site-rewrite";

    public ClosureStageResult Apply(ClosureStageInput input)
    {
        var (root, model, options, naming, statistics, diagnostics) = input;

        if (input.PlanBag is not { } bag || bag.StaticLambdas.Count == 0)
            return new ClosureStageResult(root, false);

        var rewriter = new SiteRewriter(bag.StaticLambdas, statistics);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;

        return new ClosureStageResult(rewritten, rewriter.SitesRewritten > 0);
    }

    private sealed class SiteRewriter : CSharpSyntaxRewriter
    {
        // SpanStart is unique per source occurrence; a lambda relocated into an
        // emitted method body shares its original position, so one entry serves
        // both occurrences (each is rewritten exactly once per traversal hit).
        private readonly Dictionary<(int Start, int Kind), StaticLambdaPlan> _plansByPosition;
        private readonly ClosureLiftStatistics? _statistics;

        public SiteRewriter(IReadOnlyList<StaticLambdaPlan> plans, ClosureLiftStatistics? statistics)
        {
            _statistics = statistics;
            _plansByPosition = plans.ToDictionary(
                static p => (p.SiteSpanStart, p.SiteRawKind),
                static p => p);
        }

        public int SitesRewritten { get; private set; }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            => ReplaceOrVisit(node);

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            => ReplaceOrVisit(node);

        private SyntaxNode? ReplaceOrVisit(LambdaExpressionSyntax node)
        {
            // Lookup BEFORE descending: a matched site is replaced as a whole,
            // so its body (with any nested sites) is never double-counted —
            // nested sites inside it were already substituted at emit time.
            if (_plansByPosition.TryGetValue((node.SpanStart, node.RawKind), out var plan))
            {
                SitesRewritten += 1;
                if (_statistics != null)
                    _statistics.DelegateSitesRewritten += 1;

                return StaticLambdaHandler
                    .BuildSiteReplacement(plan)
                    .WithTriviaFrom(node)
                    .WithAdditionalAnnotations(ClosureLiftAnnotations.LiftedSite(plan.MethodName));
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
