using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <summary>
/// Pass 2: Inserts temp-variable declarations collected by Pass 1
/// before their anchor statements inside each Block.
/// </summary>
internal sealed class NullCoalesceStatementLifter : CSharpSyntaxRewriter
{
    private readonly List<NullCoalesceSyntaxRewriter.PendingDeclaration> _pending;

    public NullCoalesceStatementLifter(
        List<NullCoalesceSyntaxRewriter.PendingDeclaration> pending)
    {
        _pending = pending;
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var newStatements = new List<StatementSyntax>();
        foreach (var stmt in node.Statements)
        {
            // Find pending declarations that anchor before this statement
            var pending = _pending
                .Where(d => d.AnchorStatement == stmt)
                .OrderBy(d => d.Order);
            foreach (var p in pending)
                newStatements.Add(p.Declaration);

            // Recursively process child blocks
            var visited = (StatementSyntax?)Visit(stmt) ?? stmt;
            newStatements.Add(visited);
        }
        return node.WithStatements(SyntaxFactory.List(newStatements));
    }
}
