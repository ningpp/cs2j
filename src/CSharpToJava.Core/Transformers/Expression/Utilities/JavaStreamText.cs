using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Text-level helpers for rendered Java expressions that are Stream pipelines.
/// LINQ chains are emitted as <c>StreamSupport.stream(...)</c> /
/// <c>Arrays.stream(...)</c> followed by <c>.map(...)</c>, <c>.filter(...)</c>,
/// ... and those values are <c>java.util.stream.Stream</c> instances. A Stream
/// is not an <c>Iterable</c>, so callers must materialize such an expression
/// (e.g. <c>.collect(CSharpList.toCSharpList())</c> or
/// <c>CSharpGenericIterable.from(...)</c>) before treating it as one;
/// otherwise the generated cast fails at runtime with
/// <c>ClassCastException: ReferencePipeline cannot be cast to Iterable</c>.
///
/// <para>The checks below are structural (the expression's own top-level call
/// chain), not substring scans. A stream source may itself materialize a compat
/// iterable, for example
/// <c>StreamSupport.stream(((Iterable&lt;Integer&gt;)(Iterable)
/// (CSharpGenericIterable.from(IntStream.range(0, n).boxed()
/// .collect(CSharpList.toCSharpList())))).spliterator(), false).map(...)</c>:
/// the nested <c>.collect(</c> belongs to the source argument, while the
/// expression as a whole is still a Stream. Scanning the whole text would
/// classify it as a materialized collection and make callers wrap the Stream
/// again — the exact ClassCastException this helper prevents.</para>
/// </summary>
internal static class JavaStreamText
{
    /// <summary>
    /// Expression prefixes that produce a <c>java.util.stream.Stream</c> (or a
    /// primitive stream) directly.
    /// </summary>
    private static readonly string[] StreamFactoryPrefixes =
    {
        "Arrays.stream(",
        "StreamSupport.stream(",
        "Stream.concat(",
        "Stream.of(",
        "IntStream.",
        "LongStream.",
        "DoubleStream.",
    };

    /// <summary>
    /// Top-level chain operators whose presence means the rendered value is a
    /// Stream (e.g. a Stream-typed local rendered as <c>items.map(...)</c>).
    /// </summary>
    private static readonly string[] StreamOperatorTokens =
    {
        ".stream(",
        ".filter(",
        ".map(",
        ".flatMap(",
        ".sorted(",
        ".distinct(",
        ".anyMatch(",
        ".allMatch(",
    };

    /// <summary>
    /// Terminal operators that turn a Stream into a collection/array. Only a
    /// terminal on the expression's own chain matters; nested occurrences are
    /// part of the stream's source expression.
    /// </summary>
    private static readonly string[] MaterializerTokens =
    {
        ".collect(",
        ".toArray(",
    };

    /// <summary>
    /// True when <paramref name="expression"/> renders a Java Stream pipeline
    /// (not a materialized collection/array and not a carrier call that merely
    /// contains a stream in an argument).
    /// </summary>
    public static bool LooksLikeStreamPipeline(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var text = StripOuterParentheses(expression);
        if (text.Length == 0)
            return false;

        foreach (var token in MaterializerTokens)
        {
            if (ContainsTopLevelToken(text, token))
                return false;
        }

        foreach (var prefix in StreamFactoryPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        foreach (var token in StreamOperatorTokens)
        {
            if (ContainsTopLevelToken(text, token))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Materializes a Stream pipeline through the compat Stream-to-carrier
    /// bridge (<c>CSharpGenericIterable.from(Stream)</c> collects the elements
    /// and keeps the stream's element type), so the result is a valid Iterable.
    /// </summary>
    public static string MaterializeAsIterable(string streamPipeline, ConversionContext context)
    {
        context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
        return $"CSharpGenericIterable.from({streamPipeline})";
    }

    /// <summary>
    /// Removes balanced wrapping parentheses so top-level inspection sees the
    /// expression's own call chain (e.g. <c>(StreamSupport.stream(...).map(...))</c>).
    /// </summary>
    private static string StripOuterParentheses(string expression)
    {
        var text = expression.Trim();
        while (text.Length >= 2 && text[0] == '(' && MatchingCloseIsLast(text))
            text = text[1..^1].Trim();
        return text;
    }

    private static bool MatchingCloseIsLast(string text)
    {
        var depth = 0;
        var quote = '\0';
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (escaped)
                    escaped = false;
                else if (ch == '\\')
                    escaped = true;
                else if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0)
                    return i == text.Length - 1;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="token"/> occurs at nesting depth zero of
    /// <paramref name="text"/>, i.e. on the expression's own call chain rather
    /// than inside an argument list, index/array initializer or string literal.
    /// </summary>
    private static bool ContainsTopLevelToken(string text, string token)
    {
        var depth = 0;
        var quote = '\0';
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (escaped)
                    escaped = false;
                else if (ch == '\\')
                    escaped = true;
                else if (ch == quote)
                    quote = '\0';
                continue;
            }

            switch (ch)
            {
                case '"':
                case '\'':
                    quote = ch;
                    continue;
                case '(':
                case '[':
                case '{':
                    depth++;
                    continue;
                case ')':
                case ']':
                case '}':
                    if (depth > 0)
                        depth--;
                    continue;
            }

            if (depth == 0
                && i + token.Length <= text.Length
                && string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
