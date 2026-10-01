using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// C36 collection-expression rule (fixed family order: last).
///
/// M0 (plan §3): <see cref="Analyze"/> walks every
/// <see cref="CollectionExpressionSyntax"/> site, takes the coarse
/// <c>ConvertedType</c> classification from <see cref="TargetTypeResolver"/> and
/// accumulates site counts (CS2J0570 statistics stream). It emits no
/// diagnostics per site and performs no rewrite; the family entry returns
/// Changed=false for dirty files. CE-GUARD participates through
/// <see cref="NewSyntaxGuardAnalyzer"/>'s cheap dirty scan; semantic inventory
/// lands in M3.
/// </summary>
internal sealed class CollectionExprRule : IExpressionDesugarRule
{
    public string Id => "CollectionExpr";

    public bool IsPotentiallyDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(static node => node is CollectionExpressionSyntax)
           || NewSyntaxGuardAnalyzer.ContainsGuardSyntax(root);

    public void Analyze(RuleAnalysisContext context)
    {
        var stats = context.Statistics.CollectionExpr;
        var model = context.SemanticModel;

        foreach (var node in context.Root.DescendantNodes()
                     .OfType<CollectionExpressionSyntax>())
        {
            stats.CollectionSites++;

            // Pure-syntax shape counters (safe even before semantic binding).
            if (node.Elements.Count == 0)
                stats.EmptySites++;
            if (node.Ancestors().OfType<CollectionExpressionSyntax>().Any())
                stats.NestedSites++;

            var spreadCount = node.Elements
                .Count(static element => CollectionExprSyntaxFacts.IsSpread(element));
            stats.SpreadElements += spreadCount;

            // M0: model null is defended at the family entry; keep this rule
            // crash-proof regardless (counts above still flow).
            if (model == null)
                continue;

            var resolution = TargetTypeResolver.Resolve(model, node);
            stats.AddTargetKind(resolution.Kind);

            switch (resolution.Kind)
            {
                case TargetKind.Span:
                    stats.SpanArraySites++;
                    break;
                case TargetKind.CollectionBuilder:
                    stats.BuilderSites++;
                    break;
            }

            // M0 counting only (CS2J0570). M1 adds plan construction,
            // diagnostics and the rewriter; nothing is emitted or rewritten.
        }

        // CE-GUARD semantic inventory (0584-0588) lands in M3; M0 only made
        // the file dirty through NewSyntaxGuardAnalyzer.ContainsGuardSyntax.
    }
}
