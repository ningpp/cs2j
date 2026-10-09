using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Deterministic XML doc merge (design D6): the primary part's documentation is
/// kept verbatim; every other part's doc block is wrapped into a generated
/// &lt;remarks&gt;/&lt;para source="…"&gt; section appended after it. M3 applies
/// the same rule to collapsed partial member pairs (implementation side primary).
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
            AppendSourcedDocBlock(builder, part.Declaration, part.RelativePath);
        }

        var parsed = SyntaxFactory.ParseLeadingTrivia(builder.ToString());

        // Keep non-doc leading trivia (extern pragmas etc.) from the primary part.
        var primaryNonDoc = parts[0].Declaration.GetLeadingTrivia()
            .Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        return SyntaxFactory.TriviaList(primaryNonDoc.Concat(parsed));
    }

    /// <summary>
    /// M3 member-level D6 merge: the implementation member's documentation stays
    /// verbatim; the definition side's docs are appended as a sourced remarks
    /// block. Indentation is normalized to the implementation member's original
    /// indent (factory-rebuilt modifier/attribute tokens carry no trivia).
    /// </summary>
    public static SyntaxTriviaList BuildMemberLeadingTrivia(
        SyntaxNode implementation,
        SyntaxNode definition,
        string definitionSourcePath)
    {
        var indent = MemberIndent(implementation);
        var builder = new StringBuilder();

        // Preserve non-doc, non-whitespace trivia (e.g. #pragma) from the
        // implementation side verbatim; whitespace is rebuilt below.
        foreach (var trivia in implementation.GetLeadingTrivia()
                     .Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                         && !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                         && t.Kind() != SyntaxKind.WhitespaceTrivia
                         && t.Kind() != SyntaxKind.EndOfLineTrivia))
        {
            builder.Append(trivia.ToFullString());
            if (!trivia.ToFullString().EndsWith('\n'))
            {
                builder.AppendLine();
            }
        }

        foreach (var line in ExtractRawDocCommentLines(implementation))
        {
            builder.AppendLine(indent + line.TrimStart());
        }

        AppendIndentedSourcedDocBlock(builder, definition, definitionSourcePath, indent);

        // Start a fresh physical line (the previous member/token owns the
        // separation when this node is first) and put the member on its indent.
        if (builder.Length == 0)
        {
            builder.AppendLine();
        }

        builder.Append(indent);

        return SyntaxFactory.ParseLeadingTrivia(builder.ToString());
    }

    private static void AppendIndentedSourcedDocBlock(
        StringBuilder builder,
        SyntaxNode node,
        string sourcePath,
        string indent)
    {
        var inner = ExtractDocLines(node);
        if (inner.Count == 0)
        {
            return;
        }

        builder.AppendLine(indent + "/// <remarks>");
        builder.AppendLine(indent + $"/// <para source=\"{sourcePath}\">");
        foreach (var line in inner)
        {
            builder.AppendLine(indent + "/// " + line);
        }

        builder.AppendLine(indent + "/// </para>");
        builder.AppendLine(indent + "/// </remarks>");
    }

    /// <summary>Horizontal whitespace after the last line break in a node's leading trivia.</summary>
    private static string MemberIndent(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia();
        for (var i = trivia.Count - 1; i >= 0; i--)
        {
            if (trivia[i].Kind() == SyntaxKind.WhitespaceTrivia)
            {
                var text = trivia[i].ToFullString();
                var lastBreak = text.LastIndexOf('\n');
                return lastBreak >= 0 ? text[(lastBreak + 1)..] : text;
            }

            if (trivia[i].Kind() == SyntaxKind.EndOfLineTrivia)
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static void AppendSourcedDocBlock(
        StringBuilder builder,
        SyntaxNode node,
        string sourcePath)
    {
        var inner = ExtractDocLines(node);
        if (inner.Count == 0)
        {
            return;
        }

        builder.AppendLine("/// <remarks>");
        builder.AppendLine($"/// <para source=\"{sourcePath}\">");
        foreach (var line in inner)
        {
            // Re-indent original XML content one level; keep its bytes otherwise.
            builder.AppendLine("/// " + line);
        }

        builder.AppendLine("/// </para>");
        builder.AppendLine("/// </remarks>");
    }

    private static List<string> ExtractDocLines(SyntaxNode declaration)
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

    private static List<string> ExtractRawDocCommentLines(SyntaxNode declaration)
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
