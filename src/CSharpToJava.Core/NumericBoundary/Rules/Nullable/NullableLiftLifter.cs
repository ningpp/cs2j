using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// C22 Pass 2 (design §4.5): splice every <see cref="PendingNullableLift"/>
/// declaration group before its anchor statement in the post-Pass1 tree.
/// Marker discipline mirrors the verified C17 <c>IncrementTargetLifter</c>:
/// placeholders are found on the NEW tree via
/// <see cref="NullableLiftSyntaxFacts.HoistAnnotationKind"/> annotations;
/// original-tree statement references are used only for grouping (nested
/// sites' markers live detached inside outer hoisted initializers and are not
/// visible until spliced). NL-10 standalone postfix sites additionally
/// CONSUME their anchor: the placeholder expression statement is replaced by
/// the old-value declaration plus the write statement.
/// </summary>
internal static class NullableLiftLifter
{
    public static CompilationUnitSyntax Apply(
        CompilationUnitSyntax root, IReadOnlyList<PendingNullableLift> pending)
    {
        if (pending.Count == 0)
        {
            return root;
        }

        var visibleMarkers = root
            .GetAnnotatedNodes(NullableLiftSyntaxFacts.HoistAnnotationKind)
            .ToList();

        var groupsByOriginalAnchor = pending
            .GroupBy(plan => plan.OriginalAnchor)
            .Select(group => group.OrderBy(plan => plan.Order).ToList())
            .ToList();

        // Locate each group's anchor in the NEW tree through any visible
        // placeholder (the outermost nested site; inner markers ride inside
        // the outer site's detached hoisted declarations).
        var anchorGroups = new Dictionary<StatementSyntax, List<PendingNullableLift>>();
        foreach (var groupPlans in groupsByOriginalAnchor)
        {
            StatementSyntax? newAnchor = null;
            foreach (var plan in groupPlans)
            {
                var placeholder = visibleMarkers.FirstOrDefault(node => node.HasAnnotation(plan.Marker));
                if (placeholder != null)
                {
                    newAnchor = placeholder.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
                    if (newAnchor != null)
                    {
                        break;
                    }
                }
            }

            if (newAnchor == null)
            {
                throw new InvalidOperationException(
                    "C22 nullable-lift hoist marker vanished from the rewritten tree; anchor not found");
            }

            anchorGroups[newAnchor] = groupPlans;
        }

        // Replace the anchor STATEMENTS directly: a container-based rewrite
        // rebuilds an outer container from its ORIGINAL children and silently
        // discards splices belonging to nested groups (the same nesting hazard
        // the C17 IncrementTargetLifter hit). ReplaceNodes visits bottom-up, so
        // the `updated` node already carries every nested group's result.
        var anchors = anchorGroups.Keys.ToList();

        return (CompilationUnitSyntax)root.ReplaceNodes(anchors, (original, updated) =>
            WrapInBlock((StatementSyntax)updated, anchorGroups[original]));
    }

    private static BlockSyntax WrapInBlock(
        StatementSyntax anchor,
        IReadOnlyList<PendingNullableLift> groupPlans)
    {
        var consumesAnchor = groupPlans.Any(plan => plan.ConsumesAnchor);
        if (consumesAnchor && groupPlans.Count != 1)
        {
            throw new InvalidOperationException(
                "C22 nullable-lift anchor-consuming postfix site must be the sole plan of its anchor");
        }

        var sequence = new List<StatementSyntax>();
        foreach (var plan in groupPlans)
        {
            sequence.AddRange(plan.Declarations);
            if (plan.WriteStatements != null)
            {
                sequence.AddRange(plan.WriteStatements);
            }
        }

        // Standalone postfix (`x++;` is the whole statement, including in an
        // embedded slot like `if (c) x++;`): the block carries only the
        // spliced statements; otherwise the anchor follows them.
        if (!consumesAnchor)
        {
            sequence.Add(anchor.WithoutLeadingTrivia());
        }

        return Block(sequence)
            .WithLeadingTrivia(anchor.GetLeadingTrivia())
            .WithAdditionalAnnotations(
                NB.RewriteAnnotation,
                new SyntaxAnnotation(NullableLiftSyntaxFacts.RewriteAnnotationKind));
    }
}
