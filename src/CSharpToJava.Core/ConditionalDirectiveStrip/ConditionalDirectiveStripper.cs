using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public sealed class ConditionalDirectiveStripper
{
    public ConditionalDirectiveStripResult Strip(string sourceCode, ConditionalDirectiveStripOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        options ??= new ConditionalDirectiveStripOptions();
        var diagnostics = new List<ConditionalDirectiveDiagnostic>();
        var symbols = options.Symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithPreprocessorSymbols(symbols);
        var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions);
        var root = tree.GetCompilationUnitRoot();

        foreach (var error in tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(new ConditionalDirectiveDiagnostic(
                ConditionalDirectiveSeverity.Error,
                $"{error.Id}: {error.GetMessage()}",
                error.Location.GetLineSpan().StartLinePosition.Line + 1));
        }

        if (diagnostics.Count > 0)
        {
            return new ConditionalDirectiveStripResult(sourceCode, false, diagnostics, new());
        }

        foreach (var warning in tree.GetDiagnostics().Where(diagnostic =>
                     diagnostic.Severity == DiagnosticSeverity.Warning && diagnostic.Id == "CS1030"))
        {
            diagnostics.Add(new ConditionalDirectiveDiagnostic(
                ConditionalDirectiveSeverity.Warning,
                $"#warning directive preserved: {warning.GetMessage()}",
                warning.Location.GetLineSpan().StartLinePosition.Line + 1));
        }

        if (!HasTargets(root, options.RemoveRegions))
        {
            return new ConditionalDirectiveStripResult(sourceCode, false, diagnostics, new());
        }

        var rewriter = new RemovingRewriter(options.RemoveRegions);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var output = rewritten.ToFullString();

        if (!VerifyOutput(output, parseOptions, root, options.RemoveRegions))
        {
            diagnostics.Add(new ConditionalDirectiveDiagnostic(
                ConditionalDirectiveSeverity.Error,
                "internal post-verification failed (output would not parse cleanly or token stream changed); file left unchanged"));
            return new ConditionalDirectiveStripResult(sourceCode, false, diagnostics, new());
        }

        return new ConditionalDirectiveStripResult(
            output,
            !string.Equals(sourceCode, output, StringComparison.Ordinal),
            diagnostics,
            new ConditionalDirectiveStatistics
            {
                DirectivesRemoved = rewriter.DirectivesRemoved,
                DisabledBranchesRemoved = rewriter.DisabledBranchesRemoved,
            });
    }

    internal static bool IsRemovedDirective(SyntaxKind kind, bool removeRegions = false) => kind switch
    {
        SyntaxKind.RegionDirectiveTrivia or SyntaxKind.EndRegionDirectiveTrivia => removeRegions,
        SyntaxKind.LineDirectiveTrivia
            or SyntaxKind.PragmaWarningDirectiveTrivia
            or SyntaxKind.PragmaChecksumDirectiveTrivia
            or SyntaxKind.NullableDirectiveTrivia
            or SyntaxKind.ErrorDirectiveTrivia
            or SyntaxKind.WarningDirectiveTrivia
            or SyntaxKind.ReferenceDirectiveTrivia
            or SyntaxKind.LoadDirectiveTrivia
            or SyntaxKind.ShebangDirectiveTrivia => false,
        _ => true,
    };

    internal static bool IsTargetTrivia(SyntaxTrivia trivia, bool removeRegions)
    {
        if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
        {
            return true;
        }

        if (trivia.GetStructure() is not DirectiveTriviaSyntax directive)
        {
            return false;
        }

        if (!directive.IsActive)
        {
            return true;
        }

        return IsRemovedDirective(directive.Kind(), removeRegions);
    }

    private static bool HasTargets(CompilationUnitSyntax root, bool removeRegions)
    {
        foreach (var token in root.DescendantTokens())
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                if (IsTargetTrivia(trivia, removeRegions))
                {
                    return true;
                }
            }

            foreach (var trivia in token.TrailingTrivia)
            {
                if (IsTargetTrivia(trivia, removeRegions))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool VerifyOutput(
        string output,
        CSharpParseOptions parseOptions,
        CompilationUnitSyntax originalRoot,
        bool removeRegions)
    {
        var outputRoot = CSharpSyntaxTree.ParseText(output, parseOptions).GetCompilationUnitRoot();
        if (outputRoot.SyntaxTree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return false;
        }

        if (HasTargets(outputRoot, removeRegions))
        {
            return false;
        }

        return TokenSequenceEqual(originalRoot, outputRoot);
    }

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

            if (leftTokens.Current.RawKind != rightTokens.Current.RawKind ||
                leftTokens.Current.Text != rightTokens.Current.Text)
            {
                return false;
            }
        }
    }

    private sealed class RemovingRewriter(bool removeRegions) : CSharpSyntaxRewriter
    {
        internal int DirectivesRemoved;
        internal int DisabledBranchesRemoved;

        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            var visited = base.VisitToken(token);
            var leading = Filter(visited.LeadingTrivia);
            var trailing = Filter(visited.TrailingTrivia);
            return leading.Count == visited.LeadingTrivia.Count && trailing.Count == visited.TrailingTrivia.Count
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
                if (!IsTargetTrivia(trivia, removeRegions))
                {
                    result?.Add(trivia);
                    continue;
                }

                result ??= new List<SyntaxTrivia>(triviaList.Take(index));
                if (trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                {
                    DisabledBranchesRemoved++;
                }
                else
                {
                    DirectivesRemoved++;
                }

                while (result.Count > 0 && result[^1].IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    result.RemoveAt(result.Count - 1);
                }
            }

            return result == null ? triviaList : SyntaxFactory.TriviaList(result);
        }
    }
}
