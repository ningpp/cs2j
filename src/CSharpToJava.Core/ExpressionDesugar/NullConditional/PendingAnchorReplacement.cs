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
/// scope and evaluation order. Runs inside the rule's own Rewrite, before the
/// family TempLifter.
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
}
