using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize;

/// <summary>
/// Post-rewrite verification: reparse with no errors, enabled-rule residuals
/// cleared, and either token-sequence equality (LN-1) or structural equivalence
/// (LN-7). Failure returns a diagnostic; the preprocessor rolls the file back.
/// </summary>
internal static class LexicalNormalizeVerifier
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);

    public static bool Verify(
        string output,
        CompilationUnitSyntax originalRoot,
        LexicalNormalizeOptions options,
        out LexicalNormalizeDiagnostic? failure)
    {
        failure = null;

        var reparsed = CSharpSyntaxTree.ParseText(output, ParseOptions);
        if (reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            failure = new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.StructuralCheck,
                "[LN] rewritten output produced parse errors; file rolled back");
            return false;
        }

        var outputRoot = reparsed.GetCompilationUnitRoot();

        if (options.StripLineDirectives
            && outputRoot.DescendantTrivia()
                .Select(t => t.GetStructure())
                .OfType<LineDirectiveTriviaSyntax>()
                .Any(d => d.IsActive))
        {
            failure = new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.Residual,
                "[LN-1] residual #line directive; file rolled back");
            return false;
        }

        if (options.NormalizeFileScopedNamespaces)
        {
            if (outputRoot.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Any())
            {
                failure = new LexicalNormalizeDiagnostic(
                    LexicalNormalizeSeverity.Error,
                    LexicalNormalizeCodes.Residual,
                    "[LN-7] residual file-scoped namespace; file rolled back");
                return false;
            }

            if (!StructuralEquivalence(originalRoot, outputRoot))
            {
                failure = new LexicalNormalizeDiagnostic(
                    LexicalNormalizeSeverity.Error,
                    LexicalNormalizeCodes.StructuralCheck,
                    "[LN-7] structural equivalence of usings/members failed; file rolled back");
                return false;
            }
        }
        else if (options.StripLineDirectives && !TokenSequenceEqual(originalRoot, outputRoot))
        {
            failure = new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.StructuralCheck,
                "[LN-1] token sequence mismatch; file rolled back");
            return false;
        }

        return true;
    }

    /// <summary>Using directives (with in/out namespace marker) and declared type names.</summary>
    private static bool StructuralEquivalence(CompilationUnitSyntax left, CompilationUnitSyntax right)
        => UsingShape(left).SequenceEqual(UsingShape(right), StringComparer.Ordinal)
            && MemberShape(left).SequenceEqual(MemberShape(right), StringComparer.Ordinal);

    private static IEnumerable<string> UsingShape(CompilationUnitSyntax root)
    {
        foreach (var usingDirective in root.Usings)
        {
            yield return "out:" + DirectiveScan.NormalizeUsing(usingDirective);
        }

        foreach (var namespaceDeclaration in root.Members.OfType<BaseNamespaceDeclarationSyntax>())
        {
            foreach (var usingDirective in namespaceDeclaration.Usings)
            {
                yield return "in:" + DirectiveScan.NormalizeUsing(usingDirective);
            }
        }
    }

    private static IEnumerable<string> MemberShape(CompilationUnitSyntax root)
    {
        foreach (var member in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            // BaseTypeDeclarationSyntax has no Keyword; read it per derived kind.
            var keyword = member switch
            {
                ClassDeclarationSyntax c => c.Keyword.ValueText,
                StructDeclarationSyntax s => s.Keyword.ValueText,
                InterfaceDeclarationSyntax i => i.Keyword.ValueText,
                RecordDeclarationSyntax r => r.Keyword.ValueText,
                EnumDeclarationSyntax e => e.EnumKeyword.ValueText,
                _ => member.GetType().Name,
            };

            yield return keyword + " " + member.Identifier.ValueText;
        }
    }

    // Copied from E01 (ConditionalDirectiveStripper.cs L154-178).
    private static bool TokenSequenceEqual(CompilationUnitSyntax left, CompilationUnitSyntax right)
    {
        using var leftTokens = left.DescendantTokens().GetEnumerator();
        using var rightTokens = right.DescendantTokens().GetEnumerator();
        while (true)
        {
            var hasLeft = leftTokens.MoveNext();
            var hasRight = rightTokens.MoveNext();
            if (hasLeft != hasRight)
            {
                return false;
            }

            if (!hasLeft)
            {
                return true;
            }

            var leftToken = leftTokens.Current;
            var rightToken = rightTokens.Current;
            if (leftToken.RawKind != rightToken.RawKind
                || !string.Equals(leftToken.Text, rightToken.Text, StringComparison.Ordinal))
            {
                return false;
            }
        }
    }
}
