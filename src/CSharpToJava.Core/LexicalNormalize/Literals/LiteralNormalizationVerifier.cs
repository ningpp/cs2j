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
        if (options.NormalizeRawLiterals || options.NormalizeEscEscapes)
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

        // M2 inserts the u8 blob subset block here.

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

    private static LexicalNormalizeDiagnostic Fatal(string code, string message)
        => new(LexicalNormalizeSeverity.Error, code, message);
}
