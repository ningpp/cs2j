using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// Result of LL-5: Fatal means the caller must roll the file back to the
/// original text; non-fatal diagnostics (residual Warning unless --strict)
/// are reported while the rewrite stands.
/// </summary>
internal sealed record LiteralVerificationOutcome(
    bool Fatal,
    IReadOnlyList<LexicalNormalizeDiagnostic> Diagnostics);

/// <summary>
/// LL-5 (design §4.6): reparse the output, then assert value fidelity as an
/// ordered-multiset subset (no span mapping, immune to u8 expansion shifts in
/// M2), residual freedom for enabled rules, and idempotence.
/// </summary>
internal static class LiteralNormalizationVerifier
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    public static LiteralVerificationOutcome Verify(
        string output,
        LexicalNormalizeOptions options,
        LiteralRewriteRecorder recorder)
    {
        var diagnostics = new List<LexicalNormalizeDiagnostic>();

        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(Fatal(
                LexicalNormalizeCodes.StringValueFidelity,
                "[LL-5] rewritten output produced parse errors; file rolled back"));
            return new LiteralVerificationOutcome(true, diagnostics);
        }

        var root = tree.GetCompilationUnitRoot();

        // M2 additionally gates this block on NormalizeVerbatimLiterals.
        if (options.NormalizeRawLiterals || options.NormalizeEscEscapes
            || options.NormalizeVerbatimLiterals)
        {
            var actualStrings = Multiset(
                root.DescendantTokens()
                    .Where(t => t.IsKind(SyntaxKind.StringLiteralToken)
                        && !t.Text.StartsWith("@\"", StringComparison.Ordinal))
                    .Select(t => t.ValueText));

            if (!ContainsAll(actualStrings, recorder.ExpectedStringValues))
            {
                diagnostics.Add(Fatal(
                    LexicalNormalizeCodes.StringValueFidelity,
                    "[LL-1/LL-6] regular-string value multiset is missing expected value(s); file rolled back"));
            }

            var actualChars = Multiset(
                root.DescendantTokens()
                    .Where(t => t.IsKind(SyntaxKind.CharacterLiteralToken))
                    .Select(t => (char)t.Value!));

            if (!ContainsAll(actualChars, recorder.ExpectedCharValues))
            {
                diagnostics.Add(Fatal(
                    LexicalNormalizeCodes.StringValueFidelity,
                    "[LL-6] char value multiset is missing expected value(s); file rolled back"));
            }
        }

        if (options.NormalizeUtf8Literals && recorder.ExpectedUtf8Blobs.Count > 0)
        {
            var actualBlobs = Multiset(
                root.DescendantNodes()
                    .OfType<ArrayCreationExpressionSyntax>()
                    .Select(TryExtractByteBlob)
                    .Where(blob => blob is not null)
                    .Select(blob => BlobKey(blob!)),
                StringComparer.Ordinal);

            var expectedBlobs = recorder.ExpectedUtf8Blobs.Select(BlobKey).ToList();
            if (!ContainsAll(actualBlobs, expectedBlobs))
            {
                diagnostics.Add(Fatal(
                    LexicalNormalizeCodes.Utf8ValueFidelity,
                    "[LL-2] UTF-8 byte blob multiset is missing expected sequence(s); file rolled back"));
            }
        }

        recorder.ResidualCount = CountResidual(root, options);
        if (recorder.ResidualCount > 0)
        {
            var severity = options.Strict
                ? LexicalNormalizeSeverity.Error
                : LexicalNormalizeSeverity.Warning;
            diagnostics.Add(new LexicalNormalizeDiagnostic(
                severity,
                LexicalNormalizeCodes.LiteralResidualCode,
                $"[LL-5] residual raw/\\e lexical forms remain: {recorder.ResidualCount}"));
        }

        var secondPass = new LiteralRewriter(options);
        secondPass.Visit(root);
        if (secondPass.Recorder.TotalRewrites > 0)
        {
            diagnostics.Add(Fatal(
                LexicalNormalizeCodes.LiteralResidualCode,
                "[LL-5] idempotence violation: a second literal pass rewrote node(s); file rolled back"));
        }

        var fatal = diagnostics.Any(d => d.Severity == LexicalNormalizeSeverity.Error);
        return new LiteralVerificationOutcome(fatal, diagnostics);
    }

    /// <summary>Residual scan for the rules enabled in M1 (M2 adds u8/verbatim).</summary>
    internal static int CountResidual(CompilationUnitSyntax root, LexicalNormalizeOptions options)
    {
        var residual = 0;

        foreach (var token in root.DescendantTokens())
        {
            if (options.NormalizeRawLiterals
                && (token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                    || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)))
            {
                residual++;
                continue;
            }

            // M2 inserts the u8 and verbatim residual branches here.

            if (options.NormalizeUtf8Literals
                && token.IsKind(SyntaxKind.Utf8StringLiteralToken))
            {
                residual++;
                continue;
            }

            if (options.NormalizeVerbatimLiterals
                && token.IsKind(SyntaxKind.StringLiteralToken)
                && token.Text.StartsWith("@\"", StringComparison.Ordinal))
            {
                residual++;
                continue;
            }

            if (options.NormalizeEscEscapes
                && ((token.IsKind(SyntaxKind.StringLiteralToken)
                        && !token.Text.StartsWith("@\"", StringComparison.Ordinal)
                        && LiteralSyntaxFacts.ContainsBareEsc(token.Text))
                    || (token.IsKind(SyntaxKind.CharacterLiteralToken)
                        && LiteralSyntaxFacts.ContainsBareEsc(token.Text))))
            {
                residual++;
            }
        }

        return residual;
    }

    private static Dictionary<T, int> Multiset<T>(
        IEnumerable<T> values,
        IEqualityComparer<T>? comparer = null)
        where T : notnull
    {
        var result = new Dictionary<T, int>(comparer ?? EqualityComparer<T>.Default);
        foreach (var value in values)
        {
            result[value] = result.TryGetValue(value, out var count) ? count + 1 : 1;
        }

        return result;
    }

    /// <summary>
    /// Multiset subset with consumption: every expected value must be matched
    /// by a distinct actual occurrence; pre-existing equal literals are allowed.
    /// </summary>
    private static bool ContainsAll<T>(Dictionary<T, int> actual, IReadOnlyList<T> expected)
        where T : notnull
    {
        foreach (var value in expected)
        {
            if (!actual.TryGetValue(value, out var remaining) || remaining <= 0)
            {
                return false;
            }

            actual[value] = remaining - 1;
        }

        return true;
    }

    private static string BlobKey(IReadOnlyList<byte> bytes)
        => string.Concat(bytes.Select(b => b.ToString("X2")));

    /// <summary>
    /// Recognizes only arrays shaped exactly like ByteLiteralFactory output:
    /// byte[] with omitted rank size and an initializer whose elements are all
    /// (byte) numeric constants. Anything else is a pre-existing array and is
    /// ignored (multiset subset allows extra same-shaped arrays).
    /// </summary>
    private static byte[]? TryExtractByteBlob(ArrayCreationExpressionSyntax array)
    {
        if (array.Type.ElementType is not PredefinedTypeSyntax elementType
            || !elementType.Keyword.IsKind(SyntaxKind.ByteKeyword))
        {
            return null;
        }

        if (array.Type.RankSpecifiers.Count != 1
            || array.Type.RankSpecifiers[0].Sizes.Count != 1
            || array.Type.RankSpecifiers[0].Sizes[0] is not OmittedArraySizeExpressionSyntax
            || array.Initializer is null)
        {
            return null;
        }

        var bytes = new List<byte>();
        foreach (var expression in array.Initializer.Expressions)
        {
            if (expression is not CastExpressionSyntax cast
                || cast.Type is not PredefinedTypeSyntax castType
                || !castType.Keyword.IsKind(SyntaxKind.ByteKeyword)
                || cast.Expression is not LiteralExpressionSyntax numeric
                || !numeric.IsKind(SyntaxKind.NumericLiteralExpression)
                || numeric.Token.Value is not int value
                || value < 0
                || value > 255)
            {
                return null;
            }

            bytes.Add((byte)value);
        }

        return bytes.ToArray();
    }

    private static LexicalNormalizeDiagnostic Fatal(string code, string message)
        => new(LexicalNormalizeSeverity.Error, code, message);
}
