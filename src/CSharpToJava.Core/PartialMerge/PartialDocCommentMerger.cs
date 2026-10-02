using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Deterministic XML doc merge (design D6): the primary part's documentation is
/// kept verbatim; every other part's doc block is wrapped into a generated
/// &lt;remarks&gt;/&lt;para source="…"&gt; section appended after it. Member docs
/// follow surviving members naturally; partial-pair doc merge is an M3 concern.
/// </summary>
internal static class PartialDocCommentMerger
{
    public static SyntaxTriviaList BuildTypeLeadingTrivia(TypeGroupPlan group)
    {
        var parts = group.Parts;
        var builder = new StringBuilder();

        foreach (var line in ExtractRawDocCommentLines(parts[0].Declaration))
        {
            builder.AppendLine(line);
        }

        foreach (var part in parts.Skip(1))
        {
            var inner = ExtractDocLines(part.Declaration);
            if (inner.Count == 0)
            {
                continue;
            }

            builder.AppendLine("/// <remarks>");
            builder.AppendLine($"/// <para source=\"{part.RelativePath}\">");
            foreach (var line in inner)
            {
                // Re-indent original XML content one level; keep its bytes otherwise.
                builder.AppendLine("/// " + line);
            }

            builder.AppendLine("/// </para>");
            builder.AppendLine("/// </remarks>");
        }

        var parsed = SyntaxFactory.ParseLeadingTrivia(builder.ToString());

        // Keep non-doc leading trivia (extern pragmas etc.) from the primary part.
        var primaryNonDoc = parts[0].Declaration.GetLeadingTrivia()
            .Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        return SyntaxFactory.TriviaList(primaryNonDoc.Concat(parsed));
    }

    private static List<string> ExtractDocLines(TypeDeclarationSyntax declaration)
    {
        // Content after '///' (XML only); summary wrappers of secondary parts are
        // dropped — their text is nested under the primary's remarks instead.
        return ExtractRawDocCommentLines(declaration)
            .Select(line => line.StartsWith("///") ? line[3..].TrimStart() : line.Trim())
            .Where(line =>
                !line.StartsWith("<summary", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("</summary", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<string> ExtractRawDocCommentLines(TypeDeclarationSyntax declaration)
    {
        var trivia = declaration.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .ToList();
        if (trivia.Count == 0)
        {
            return [];
        }

        return declaration.SyntaxTree.GetText()
            .Lines
            // Structured trivia Span excludes the '///' marker; FullSpan covers it.
            .Where(line => trivia.Any(t => t.FullSpan.Contains(line.Span.Start)))
            .Select(line => line.ToString().Trim())
            .ToList();
    }
}
