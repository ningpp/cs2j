using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// C22 M1 Pass 2 (design §4.5): splice every <see cref="PendingNullableLift"/>
/// declaration group before its anchor statement in the post-Pass1 tree.
/// Marker discipline mirrors the verified C17 <c>IncrementTargetLifter</c>:
/// placeholders are found on the NEW tree via
/// <see cref="NullableLiftSyntaxFacts.HoistAnnotationKind"/> annotations;
/// original-tree statement references are used only for grouping (nested
/// sites' markers live detached inside outer hoisted initializers and are not
/// visible until spliced). C22 only ever has consumed (value-context) sites,
/// so there is no standalone block-replacement branch.
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

        var parents = anchorGroups.Keys
            .Select(anchor => anchor.Parent!)
            .Distinct()
            .ToList();

        return (CompilationUnitSyntax)root.ReplaceNodes(parents, (parent, _) =>
        {
            if (parent is BlockSyntax block)
            {
                return block.WithStatements(List(ExpandStatements(block.Statements, anchorGroups)));
            }

            if (parent is SwitchSectionSyntax section)
            {
                return section.WithStatements(List(ExpandStatements(section.Statements, anchorGroups)));
            }

            // Embedded statement slot (if/while/do/for/foreach/using/lock/
            // fixed/else/labeled, or a GlobalStatementSyntax wrapping a
            // top-level statement): exactly one anchor group, wrap the
            // declarations plus the anchor in a bare block.
            var (anchor, groupPlans) = anchorGroups.Single(pair => pair.Key.Parent == parent);
            return parent.ReplaceNode(anchor, WrapInBlock(anchor, groupPlans));
        });
    }

    private static IEnumerable<StatementSyntax> ExpandStatements(
        SyntaxList<StatementSyntax> statements,
        IReadOnlyDictionary<StatementSyntax, List<PendingNullableLift>> anchorGroups)
    {
        foreach (var statement in statements)
        {
            if (!anchorGroups.TryGetValue(statement, out var groupPlans))
            {
                yield return statement;
                continue;
            }

            // Consumed sites: declarations (inner sites first via Order —
            // outer initializers read the inner temps) precede the anchor.
            var leading = statement.GetLeadingTrivia();
            var emittedAny = false;
            foreach (var plan in groupPlans)
            {
                foreach (var hoisted in plan.Declarations)
                {
                    yield return emittedAny
                        ? hoisted
                        : hoisted.WithLeadingTrivia(leading);
                    emittedAny = true;
                }
            }

            yield return statement.WithoutLeadingTrivia()
                .WithLeadingTrivia(ElasticMarker);
        }
    }

    private static BlockSyntax WrapInBlock(
        StatementSyntax anchor,
        IReadOnlyList<PendingNullableLift> groupPlans)
    {
        var sequence = new List<StatementSyntax>();
        foreach (var plan in groupPlans)
        {
            sequence.AddRange(plan.Declarations);
        }

        sequence.Add(anchor.WithoutLeadingTrivia());

        return Block(sequence)
            .WithLeadingTrivia(anchor.GetLeadingTrivia())
            .WithAdditionalAnnotations(
                NB.RewriteAnnotation,
                new SyntaxAnnotation(NullableLiftSyntaxFacts.RewriteAnnotationKind));
    }
}
