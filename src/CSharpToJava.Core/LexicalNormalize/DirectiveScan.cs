using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LexicalNormalize;

internal sealed record DirectiveScanOutcome(
    List<LexicalNormalizeDiagnostic> Diagnostics,
    List<GlobalUsingRecord> GlobalUsings,
    MutableLexicalStats Stats);

/// <summary>
/// Read-only guard/audit scan: LN-2 active #error, LN-3 residual conditional
/// directives, LN-4 extern alias declarations/use sites, LN-5 global using
/// classification and LN-6 static using inventory. Never rewrites nodes.
/// </summary>
internal static class DirectiveScan
{
    public static DirectiveScanOutcome Run(CompilationUnitSyntax root, LexicalNormalizeOptions options)
    {
        var diagnostics = new List<LexicalNormalizeDiagnostic>();
        var globalUsings = new List<GlobalUsingRecord>();
        var stats = new MutableLexicalStats();

        var activeDirectives = root.DescendantTrivia()
            .Select(trivia => trivia.GetStructure())
            .OfType<DirectiveTriviaSyntax>()
            .Where(directive => directive.IsActive)
            .ToList();

        // LN-2: active #error must never be stripped; fail loudly.
        foreach (var error in activeDirectives.OfType<ErrorDirectiveTriviaSyntax>())
        {
            stats.ErrorDirectivesSeen++;
            stats.AddError(LexicalNormalizeCodes.ErrorDirective);
            diagnostics.Add(new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.ErrorDirective,
                $"[LN-2] active #error directive must be resolved: {error.ToString().Trim()}",
                LineOf(error)));
        }

        // LN-3: conditional family must already have been removed by E01.
        foreach (var directive in activeDirectives)
        {
            if (directive is DefineDirectiveTriviaSyntax
                or UndefDirectiveTriviaSyntax
                or IfDirectiveTriviaSyntax
                or ElifDirectiveTriviaSyntax
                or ElseDirectiveTriviaSyntax
                or EndIfDirectiveTriviaSyntax)
            {
                stats.ResidualConditionalDirectives++;
                stats.AddError(LexicalNormalizeCodes.ConditionalFirst);
                diagnostics.Add(new LexicalNormalizeDiagnostic(
                    LexicalNormalizeSeverity.Error,
                    LexicalNormalizeCodes.ConditionalFirst,
                    "[LN-3] residual conditional directive; the 'conditional' pass must run before lexical-normalize",
                    LineOf(directive)));
            }
        }

        foreach (var trivia in root.DescendantTrivia()
                     .Where(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            stats.ResidualConditionalDirectives++;
            stats.AddError(LexicalNormalizeCodes.ConditionalFirst);
            diagnostics.Add(new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                LexicalNormalizeCodes.ConditionalFirst,
                "[LN-3] residual disabled text; the 'conditional' pass must run before lexical-normalize",
                LineOf(trivia)));
        }

        // LN-4a: extern alias declarations.
        foreach (var externAlias in root.DescendantNodes().OfType<ExternAliasDirectiveSyntax>())
        {
            stats.ExternAliasDirectives++;
            if (options.ExternAliasSeverity == LexicalNormalizeSeverity.Error)
            {
                stats.AddError(LexicalNormalizeCodes.ExternAlias);
            }

            diagnostics.Add(new LexicalNormalizeDiagnostic(
                options.ExternAliasSeverity,
                LexicalNormalizeCodes.ExternAlias,
                $"[LN-4] extern alias cannot be normalized by a syntax-only pass: {externAlias.ToString().Trim()}",
                LineOf(externAlias)));
        }

        // LN-4b: alias-qualified use sites (global:: is built-in and excluded).
        foreach (var aliasName in root.DescendantNodes().OfType<AliasQualifiedNameSyntax>()
                     .Where(name => name.Alias.Identifier.ValueText != "global"))
        {
            stats.ExternAliasQualifiedSites++;
            if (options.ExternAliasSeverity == LexicalNormalizeSeverity.Error)
            {
                stats.AddError(LexicalNormalizeCodes.ExternAlias);
            }

            diagnostics.Add(new LexicalNormalizeDiagnostic(
                options.ExternAliasSeverity,
                LexicalNormalizeCodes.ExternAlias,
                $"[LN-4] alias-qualified name cannot be normalized by a syntax-only pass: {aliasName.ToString().Trim()}",
                LineOf(aliasName)));
        }

        // LN-5/LN-6: using inventory.
        foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var isStatic = usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword);
            var isGlobal = usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword);
            var isAlias = usingDirective.Alias != null;

            if (isStatic)
            {
                stats.StaticUsings++;
            }

            if (!isGlobal)
            {
                continue;
            }

            var kind = isAlias ? GlobalUsingKind.Alias
                : isStatic ? GlobalUsingKind.Static
                : GlobalUsingKind.Plain;

            if (kind == GlobalUsingKind.Plain)
            {
                stats.GlobalUsingsPlain++;
            }
            else if (kind == GlobalUsingKind.Static)
            {
                stats.GlobalUsingsStatic++;
            }
            else
            {
                stats.GlobalUsingsAlias++;
            }

            globalUsings.Add(new GlobalUsingRecord(
                kind,
                NormalizeUsing(usingDirective),
                LineOf(usingDirective) ?? 0,
                isStatic,
                isAlias));
        }

        return new DirectiveScanOutcome(diagnostics, globalUsings, stats);
    }

    /// <summary>Ordinal text normalization for duplicate comparison (trim + single spaces).</summary>
    internal static string NormalizeUsing(UsingDirectiveSyntax usingDirective)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(
            usingDirective.ToString(), @"\s+", " ");
        return text.Trim();
    }

    internal static int? LineOf(SyntaxNode node)
    {
        try
        {
            return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static int? LineOf(SyntaxTrivia trivia)
    {
        try
        {
            return trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
