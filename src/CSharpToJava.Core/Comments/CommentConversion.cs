using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Comments;

internal sealed class ConvertedCommentSet
{
    public string? DocumentationComment { get; init; }
    public string? RegularComment { get; init; }

    public string? ToCombinedComment()
    {
        return JoinComments(RegularComment, DocumentationComment);
    }

    public static string? JoinComments(params string?[] comments)
    {
        var parts = comments.Where(static part => !string.IsNullOrWhiteSpace(part)).ToList();
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }
}

internal static class CommentConversion
{
    public static ConvertedCommentSet ExtractDeclarationComments(SyntaxNode node, ISymbol? symbol, ConversionContext context)
    {
        var regularComment = ExtractRegularLeadingComments(node.GetLeadingTrivia());
        var documentationComment = context.Options.GenerateJavaDoc
            ? ConvertDocumentationComment(symbol)
            : null;

        return new ConvertedCommentSet
        {
            DocumentationComment = documentationComment,
            RegularComment = regularComment
        };
    }

    public static string? ExtractStatementLeadingComments(StatementSyntax statement)
    {
        return ExtractRegularLeadingComments(statement.GetLeadingTrivia());
    }

    public static string? ExtractStatementTrailingComments(StatementSyntax statement)
    {
        var trailingComments = statement.GetTrailingTrivia()
            .Where(IsRegularCommentTrivia)
            .Select(FormatRegularCommentTrivia)
            .Where(static comment => !string.IsNullOrWhiteSpace(comment))
            .ToList();

        return trailingComments.Count == 0 ? null : string.Join("\n", trailingComments);
    }

    private static string? ConvertDocumentationComment(ISymbol? symbol)
    {
        if (symbol == null)
            return null;

        string? xml;
        try
        {
            xml = symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: default);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Roslyn bug: DocumentationCommentCompiler.WriteSubStringLine can
            // compute a negative length for certain malformed doc comments.
            return null;
        }
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root == null)
                return null;

            var sections = new List<string>();
            var summary = ConvertBlock(root.Element("summary"));
            if (!string.IsNullOrWhiteSpace(summary))
                sections.Add(summary);

            var remarks = ConvertBlock(root.Element("remarks"));
            if (!string.IsNullOrWhiteSpace(remarks))
                sections.Add(remarks);

            var paramTags = root.Elements("param")
                .Select(param => new
                {
                    Name = param.Attribute("name")?.Value,
                    Description = ConvertBlock(param)
                })
                .Where(static param => !string.IsNullOrWhiteSpace(param.Name) && !string.IsNullOrWhiteSpace(param.Description))
                .Select(param => $"@param {param.Name} {param.Description}")
                .ToList();

            var typeParamTags = root.Elements("typeparam")
                .Select(param => new
                {
                    Name = param.Attribute("name")?.Value,
                    Description = ConvertBlock(param)
                })
                .Where(static param => !string.IsNullOrWhiteSpace(param.Name) && !string.IsNullOrWhiteSpace(param.Description))
                .Select(param => $"@param <{param.Name}> {param.Description}")
                .ToList();

            var returns = ConvertBlock(root.Element("returns"));
            var exceptions = root.Elements()
                .Where(element => element.Name.LocalName is "exception" or "throws")
                .Select(element => new
                {
                    TypeName = SimplifyCref(element.Attribute("cref")?.Value),
                    Description = ConvertBlock(element)
                })
                .Where(static item => !string.IsNullOrWhiteSpace(item.TypeName) && !string.IsNullOrWhiteSpace(item.Description))
                .Select(item => $"@throws {item.TypeName} {item.Description}")
                .ToList();

            var lines = new List<string> { "/**" };
            AppendBlock(lines, sections);
            AppendTags(lines, paramTags);
            AppendTags(lines, typeParamTags);
            if (!string.IsNullOrWhiteSpace(returns))
                lines.Add($" * @return {returns}");
            foreach (var exception in exceptions)
                lines.Add($" * {exception}");
            lines.Add(" */");

            var result = lines.Count > 2 ? string.Join("\n", lines) : null;
            return EscapeUnicodeInComment(result);
        }
        catch
        {
            return null;
        }
    }

    private static void AppendBlock(List<string> lines, List<string> sections)
    {
        for (int index = 0; index < sections.Count; index++)
        {
            if (index > 0)
                lines.Add(" *");

            foreach (var line in SplitLines(sections[index]))
            {
                lines.Add(string.IsNullOrWhiteSpace(line) ? " *" : $" * {line}");
            }
        }
    }

    private static void AppendTags(List<string> lines, List<string> tags)
    {
        if (tags.Count == 0)
            return;

        bool hasBody = lines.Count > 1;
        if (hasBody)
            lines.Add(" *");

        foreach (var tag in tags)
            lines.Add($" * {tag}");
    }

    private static string? ExtractRegularLeadingComments(SyntaxTriviaList triviaList)
    {
        var comments = triviaList
            .Where(IsRegularCommentTrivia)
            .Select(FormatRegularCommentTrivia)
            .Where(static comment => !string.IsNullOrWhiteSpace(comment))
            .ToList();

        return comments.Count == 0 ? null : EscapeUnicodeInComment(string.Join("\n", comments));
    }

    private static bool IsRegularCommentTrivia(SyntaxTrivia trivia)
    {
        return trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia);
    }

    private static string FormatRegularCommentTrivia(SyntaxTrivia trivia)
    {
        var text = trivia.ToFullString().Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        if (!text.Contains('\n'))
            return text;

        var lines = text.Split('\n');
        return string.Join("\n", lines.Select(static line => line.TrimEnd()));
    }

    private static string? ConvertBlock(XElement? element)
    {
        if (element == null)
            return null;

        var builder = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            builder.Append(ConvertInlineNode(node));
        }

        return NormalizeWhitespace(builder.ToString());
    }

    private static string ConvertInlineNode(XNode node)
    {
        return node switch
        {
            XText text => text.Value,
            XElement element => ConvertInlineElement(element),
            _ => string.Empty
        };
    }

    private static string ConvertInlineElement(XElement element)
    {
        var content = string.Concat(element.Nodes().Select(ConvertInlineNode));
        return element.Name.LocalName switch
        {
            "see" => ConvertSeeElement(element),
            "seealso" => ConvertSeeElement(element),
            "paramref" => element.Attribute("name")?.Value ?? content,
            "typeparamref" => element.Attribute("name")?.Value ?? content,
            "c" => $"{{@code {NormalizeWhitespace(content)}}}",
            "code" => WrapCodeBlock(content),
            "para" => "\n\n" + NormalizeWhitespace(content),
            _ => content
        };
    }

    private static string ConvertSeeElement(XElement element)
    {
        var cref = SimplifyCref(element.Attribute("cref")?.Value);
        if (!string.IsNullOrWhiteSpace(cref))
            return $"{{@link {cref}}}";

        var href = element.Attribute("href")?.Value;
        if (!string.IsNullOrWhiteSpace(href))
            return href;

        return NormalizeWhitespace(string.Concat(element.Nodes().Select(ConvertInlineNode)));
    }

    private static string WrapCodeBlock(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ');
        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : $"<pre>{{@code\n{normalized}\n}}</pre>";
    }

    private static string SimplifyCref(string? cref)
    {
        if (string.IsNullOrWhiteSpace(cref))
            return string.Empty;

        var value = cref.Trim();
        var colonIndex = value.IndexOf(':');
        if (colonIndex >= 0 && colonIndex < value.Length - 1)
            value = value[(colonIndex + 1)..];

        return value.Replace('#', '.');
    }

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n')
            .Select(static line => System.Text.RegularExpressions.Regex.Replace(line.Trim(), "\\s+", " "))
            .ToList();

        var collapsed = string.Join("\n", lines);
        collapsed = System.Text.RegularExpressions.Regex.Replace(collapsed, "\n{3,}", "\n\n");
        return collapsed.Trim();
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>
    /// Escapes Java Unicode escape sequences in comment text.
    /// Java's compiler processes \uXXXX sequences even inside comments, which causes
    /// compilation errors when C# doc comments contain literal \u or \U text
    /// (e.g., '\uxxxx\uxxxx' in a remarks block). Replace \u with \\u to prevent this.
    /// </summary>
    private static string? EscapeUnicodeInComment(string? text)
    {
        if (text == null)
            return null;
        // Replace \u followed by any characters with \\u to prevent the Java compiler
        // from interpreting \uXXXX in comments. We must handle both valid hex sequences
        // (like \u0041) and invalid ones (like \uxxxx) since Java tries to parse both.
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"\\([uU])", @"\\$1");
    }
}