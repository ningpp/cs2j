using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize;

/// <summary>
/// LN-1: removes active <see cref="LineDirectiveTriviaSyntax"/> structured trivia
/// using the same filter-and-rewind mechanism as E01's RemovingRewriter
/// (ConditionalDirectiveStripper.cs L180-230). The target table is independent
/// and contains only #line; the token sequence (kind+text) must stay identical.
/// </summary>
internal sealed class LineDirectiveRewriter : CSharpSyntaxRewriter
{
    private int _lineDirectivesRemoved;

    public int LineDirectivesRemoved => _lineDirectivesRemoved;

    public override SyntaxToken VisitToken(SyntaxToken token)
    {
        var visited = base.VisitToken(token);
        var leading = Filter(visited.LeadingTrivia);
        var trailing = Filter(visited.TrailingTrivia);

        return leading.Count == visited.LeadingTrivia.Count
            && trailing.Count == visited.TrailingTrivia.Count
                ? visited
                : visited.WithLeadingTrivia(leading).WithTrailingTrivia(trailing);
    }

    private SyntaxTriviaList Filter(SyntaxTriviaList triviaList)
    {
        if (triviaList.Count == 0)
        {
            return triviaList;
        }

        List<SyntaxTrivia>? result = null;
        for (var index = 0; index < triviaList.Count; index++)
        {
            var trivia = triviaList[index];
            if (!IsLineTarget(trivia))
            {
                result?.Add(trivia);
                continue;
            }

            result ??= new List<SyntaxTrivia>(triviaList.Take(index));
            _lineDirectivesRemoved++;

            // Rewind trailing whitespace left on the line (E01 mechanism).
            while (result.Count > 0 && result[^1].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                result.RemoveAt(result.Count - 1);
            }
        }

        return result == null ? triviaList : SyntaxFactory.TriviaList(result);
    }

    internal static bool IsLineTarget(SyntaxTrivia trivia)
        => trivia.GetStructure() is LineDirectiveTriviaSyntax { IsActive: true };
}
