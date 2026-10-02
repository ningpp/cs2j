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

            // A standalone postfix (`x++;`) is the whole statement: its
            // placeholder statement is dropped and replaced by the spliced
            // old-value declaration + write. Such a site's old-value
            // initializer only reads the (simple) target, so no inner-site
            // plan can share its anchor.
            var consumesAnchor = groupPlans.Any(plan => plan.ConsumesAnchor);
            if (consumesAnchor && groupPlans.Count != 1)
            {
                throw new InvalidOperationException(
                    "C22 nullable-lift anchor-consuming postfix site must be the sole plan of its anchor");
            }

            // Consumed sites: declarations (inner sites first via Order —
            // outer initializers read the inner temps) precede the anchor;
            // NL-10 writes follow their site's declarations.
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

                if (plan.WriteStatements == null)
                {
                    continue;
                }

                foreach (var write in plan.WriteStatements)
                {
                    yield return emittedAny
                        ? write
                        : write.WithLeadingTrivia(leading);
                    emittedAny = true;
                }
            }

            if (consumesAnchor)
            {
                continue;
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
            if (plan.WriteStatements != null)
            {
                sequence.AddRange(plan.WriteStatements);
            }
        }

        // Standalone postfix in an embedded slot (e.g. `if (c) x++;`): the
        // block carries only the spliced statements.
        if (!groupPlans.Any(plan => plan.ConsumesAnchor))
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
