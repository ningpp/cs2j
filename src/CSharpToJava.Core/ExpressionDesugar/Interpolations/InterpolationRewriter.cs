using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// Bottom-up rewriter for C23 (design §4.1 step 2, IE-1~IE-5). Inner
/// interpolations expand first (inner Concat becomes a string-typed argument
/// of the outer site). All semantic decisions come from Analyze-built plans
/// keyed by ORIGINAL nodes; rendering uses the already-visited children, so
/// hole expressions are zero-copied and appear exactly once.
///
/// Every preserved path (non-constant alignment / FormattableString /
/// expression tree / Keep / errors) keeps the node and tags it with the
/// shared explained annotation so the residual gate can pair it.
/// </summary>
internal sealed class InterpolationRewriter : CSharpSyntaxRewriter
{
    private readonly IReadOnlyDictionary<InterpolatedStringExpressionSyntax, InterpolationPlan> _plans;

    public InterpolationRewriter(
        IReadOnlyDictionary<InterpolatedStringExpressionSyntax, InterpolationPlan> plans)
    {
        _plans = plans;
    }

    /// <summary>Number of sites actually rewritten (Fold/Concat/Format; drives the dirty gate).</summary>
    public int RewriteCount { get; private set; }

    public override SyntaxNode? VisitInterpolatedStringExpression(
        InterpolatedStringExpressionSyntax node)
    {
        var visited = (InterpolatedStringExpressionSyntax)
            base.VisitInterpolatedStringExpression(node)!;

        if (!_plans.TryGetValue(node, out var plan))
            return visited;

        switch (plan.Path)
        {
            case InterpolationPath.Fold:
            {
                RewriteCount++;
                return ConcatEmitter.EmitFold(plan.ConstantValue)
                    .WithTriviaFrom(visited);
            }

            case InterpolationPath.Concat:
            {
                RewriteCount++;
                return ConcatEmitter.EmitConcat(visited, plan)
                    .WithTriviaFrom(visited);
            }

            case InterpolationPath.Format:
            {
                RewriteCount++;
                return FormatEmitter.EmitFormat(visited, plan)
                    .WithTriviaFrom(visited);
            }

            default:
                // Every non-rewrite path is an explained preservation: the
                // node stays compilable C# and pairs with the residual gate.
                return visited.WithAdditionalAnnotations(
                    ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
        }
    }
}
