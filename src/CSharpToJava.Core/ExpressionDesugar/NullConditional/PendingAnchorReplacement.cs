using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// Registration of an anchor statement that must be <b>replaced</b> (not merely
/// prefixed) by the C35 rule (design §4.4): void/statement <c>a?.M();</c>
/// sites become segment declarations plus nested if blocks. The capability is
/// additive and lives entirely inside this directory (the shared family
/// TempLifter only inserts statements before anchors).
/// </summary>
/// <param name="AnchorOriginal">Original anchor statement object reference.</param>
/// <param name="Marker">Annotation attached to the anchor during the rewrite so it stays locatable.</param>
/// <param name="Statements">Prepared replacement statement sequence.</param>
internal sealed record PendingAnchorReplacement(
    SyntaxNode AnchorOriginal,
    SyntaxAnnotation Marker,
    IReadOnlyList<StatementSyntax> Statements);

/// <summary>
/// Applies pending anchor replacements on a rewritten tree. When the anchor is
/// a direct statement of a <see cref="BlockSyntax"/> the replacement sequence
/// is spliced flat into that block; otherwise (embedded statement or
/// switch-section statement) the sequence is wrapped in one block, preserving
/// scope and evaluation order. A void expression-bodied member
/// (<see cref="ArrowExpressionClauseSyntax"/>) is converted to a block body
/// whose statements are the lowered chain (the arrow expression itself was the
/// whole chain, so no tail statement remains). Runs inside the rule's own
/// Rewrite, before the family TempLifter.
/// </summary>
internal static class PendingAnchorReplacementApplicator
{
    public static CompilationUnitSyntax Apply(
        CompilationUnitSyntax root,
        IReadOnlyList<PendingAnchorReplacement> pending)
    {
        if (pending.Count == 0)
            return root;

        SyntaxNode current = root;
        foreach (var entry in pending)
        {
            var anchorNode = current.GetAnnotatedNodes(entry.Marker)
                .FirstOrDefault();

            if (anchorNode is ArrowExpressionClauseSyntax arrow)
            {
                current = ExpandVoidArrowBody(current, arrow, entry);
                continue;
            }

            if (anchorNode is not StatementSyntax anchor)
                continue;

            if (anchor.Parent is BlockSyntax block)
            {
                var index = block.Statements.IndexOf(anchor);
                var newStatements = block.Statements
                    .Take(index)
                    .Concat(entry.Statements)
                    .Concat(block.Statements.Skip(index + 1));
                var newBlock = block.WithStatements(
                    SyntaxFactory.List(newStatements));
                current = current.ReplaceNode(block, newBlock);
            }
            else
            {
                var wrapped = SyntaxFactory.Block(
                    SyntaxFactory.List(entry.Statements));
                current = current.ReplaceNode(anchor, wrapped);
            }
        }

        return (CompilationUnitSyntax)current;
    }

    /// <summary>
    /// Converts an expression-bodied void member into a block body containing
    /// the lowered guard chain. Annotations carried by the arrow clause (family
    /// lift anchors stamped by this or another rule on the same expression)
    /// move to the first replacement statement so a later family lift still
    /// splices its pending declarations before the guard chain.
    /// </summary>
    private static SyntaxNode ExpandVoidArrowBody(
        SyntaxNode current,
        ArrowExpressionClauseSyntax arrow,
        PendingAnchorReplacement entry)
    {
        var statements = entry.Statements;
        var carried = arrow
            .GetAnnotations(ExpressionDesugarTempLifter.AnchorAnnotationKind)
            .ToArray();
        if (carried.Length > 0)
        {
            var withAnnotations = new List<StatementSyntax>(statements.Count)
            {
                statements[0].WithAdditionalAnnotations(carried),
            };
            for (var i = 1; i < statements.Count; i++)
                withAnnotations.Add(statements[i]);
            statements = withAnnotations;
        }

        var block = SyntaxFactory.Block(SyntaxFactory.List(statements));
        var member = arrow.Parent!;
        SyntaxNode updated = member switch
        {
            MethodDeclarationSyntax method => method.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            LocalFunctionStatementSyntax local => local.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            ConstructorDeclarationSyntax constructor =>
                constructor.WithExpressionBody(null)
                    .WithSemicolonToken(default).WithBody(block),
            DestructorDeclarationSyntax destructor => destructor
                .WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(block),
            AccessorDeclarationSyntax accessor => accessor
                .WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(block),
            _ => member,
        };

        return ReferenceEquals(updated, member)
            ? current
            : current.ReplaceNode(member, updated);
    }
}
