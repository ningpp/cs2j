using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.CompatMethodReplacement;

/// <summary>
/// Independent Expression pass <c>compat-method-replacement</c> (runs after
/// <c>fixed-span-to-array</c> and before <c>unsafe-ffm</c>). It looks up each
/// method declaration in the code-registered
/// <see cref="CompatMethodReplacementRegistry"/> and, when a known method's
/// fully-qualified signature matches, replaces only its body with the entry's
/// managed implementation. Signature, attributes and modifiers (including
/// <c>unsafe</c>, which <c>unsafe-ffm</c> removes later) are preserved.
/// <para>
/// The built-in entries replace the <c>JsonWriterHelper.ToUtf8</c> family —
/// the Runtime 10 form
/// <c>ToUtf8(ReadOnlySpan&lt;char&gt;, Span&lt;byte&gt;, out int)</c>
/// (pointer/Utf8.FromUtf16 transcoder) and the legacy 3.x 4-param byte-span
/// form — with an <c>Encoding.UTF8.GetEncoder().Convert</c> implementation,
/// and the char-span
/// <c>JsonWriterHelper.NeedsEscaping(ReadOnlySpan&lt;char&gt;, JavaScriptEncoder?)</c>
/// — a <c>fixed (char*)</c> scan through
/// <c>FindFirstCharacterToEncode(char*, int)</c> — with a managed per-character
/// <c>WillEncode</c> loop. That removes those methods' <c>fixed</c> statements
/// and pointer arguments so <c>unsafe-ffm</c> no longer has to reject them.
/// </para>
/// <para>
/// Only exact symbol matches are rewritten; same-named overloads, same-named
/// types in another namespace and every other method stay verbatim, with
/// CMR001 for a near miss. The replacement body carries a
/// <c>cs2j-compat-method-replacement</c> marker comment, so re-running the pass
/// on its own output is a no-op (idempotent). Clean files are byte-for-byte
/// identical. The adapter additionally runs the W-09 drift gate
/// (<see cref="CompatMethodReplacementDriftDetector"/>): an entry family whose
/// declaring type exists in the corpus but matched nothing is a CMR020 Error,
/// so a library upgrade can never silently invalidate the table.
/// </para>
/// </summary>
public sealed class CompatMethodReplacementPreprocessor
{
    /// <summary>Sentinel comment identifying a body this pass already produced.</summary>
    public const string ReplacementMarker = "cs2j-compat-method-replacement";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private readonly CompatMethodReplacementOptions _options;

    public CompatMethodReplacementPreprocessor(CompatMethodReplacementOptions? options = null)
    {
        _options = options ?? new CompatMethodReplacementOptions();
    }

    /// <summary>Rewrite one file using the caller's whole-tree semantic model.</summary>
    public CompatMethodReplacementResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new CompatMethodReplacementStatistics();
        var diagnostics = new List<CompatMethodReplacementDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // cs2j-generated support/registry artifacts are not user method surface.
        if (IsCs2jGeneratedSupportFile(filePath) || !_options.Enabled)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var rewriter = new ReplacementRewriter(
            _options.Registry,
            semanticModel,
            statistics,
            diagnostics,
            filePath);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (statistics.MethodsReplaced == 0)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var outputCode = newRoot.ToFullString();
        if (!ParsesWithoutErrors(outputCode))
        {
            statistics.MethodsReplaced = 0;
            statistics.FilesSkippedClean++;
            statistics.MatchedEntryKeys.Clear();
            diagnostics.Add(new CompatMethodReplacementDiagnostic(
                CompatMethodReplacementSeverity.Warning,
                CompatMethodReplacementDiagnosticCode.CMR010,
                "compat-method-replacement rewrite failed local parse validation; the original " +
                "file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        return new CompatMethodReplacementResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    // ---- rewrite engine ----

    private sealed class ReplacementRewriter : CSharpSyntaxRewriter
    {
        private readonly CompatMethodReplacementRegistry _registry;
        private readonly SemanticModel _model;
        private readonly CompatMethodReplacementStatistics _statistics;
        private readonly List<CompatMethodReplacementDiagnostic> _diagnostics;
        private readonly string _filePath;

        public ReplacementRewriter(
            CompatMethodReplacementRegistry registry,
            SemanticModel model,
            CompatMethodReplacementStatistics statistics,
            List<CompatMethodReplacementDiagnostic> diagnostics,
            string filePath)
        {
            _registry = registry;
            _model = model;
            _statistics = statistics;
            _diagnostics = diagnostics;
            _filePath = filePath;
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (_model.GetDeclaredSymbol(node) is not IMethodSymbol symbol)
                return base.VisitMethodDeclaration(node);

            // W-09 drift evidence: every visited method's containing type is
            // recorded (normalized namespace|type) so the whole-tree gate can
            // distinguish "entry unused because the corpus lacks the library"
            // from "the declaring type exists but the signature drifted".
            _statistics.DeclaredTypeKeys.Add(CompatMethodReplacementEntry.TypeKeyFor(symbol));

            var identityMatches = _registry.Entries
                .Where(entry => entry.MatchesDeclarationIdentity(symbol))
                .ToList();
            if (identityMatches.Count == 0)
                return base.VisitMethodDeclaration(node);

            if (IsAlreadyReplaced(node))
            {
                // The marker proves an earlier run (or this pass earlier in the
                // chain) produced the body; never rewrite it again.
                _statistics.MethodsAlreadyReplaced++;
                foreach (var entry in identityMatches)
                    _statistics.MatchedEntryKeys.Add(entry.Key);
                return base.VisitMethodDeclaration(node);
            }

            CompatMethodReplacementEntry? match = null;
            string? mismatchReason = null;
            foreach (var entry in identityMatches)
            {
                if (entry.Matches(symbol, out var reason))
                {
                    match = entry;
                    break;
                }

                mismatchReason ??= reason;
            }

            if (match is null)
            {
                _statistics.SignatureMismatches++;
                _diagnostics.Add(new CompatMethodReplacementDiagnostic(
                    CompatMethodReplacementSeverity.Warning,
                    CompatMethodReplacementDiagnosticCode.CMR001,
                    $"compat method replacement entry '{identityMatches[0].Key}' did not match " +
                    $"declaration '{CompatMethodReplacementEntry.Describe(symbol)}': {mismatchReason}",
                    _filePath,
                    LineOf(node),
                    ColumnOf(node)));
                return base.VisitMethodDeclaration(node);
            }

            var originalBody = node.Body;
            var replacementBody = BuildReplacementBody(match, symbol, originalBody);

            _statistics.MethodsReplaced++;
            _statistics.MatchedEntryKeys.Add(match.Key);

            if (originalBody is not null)
                return node.WithBody(replacementBody);

            // Expression-bodied known method (unlikely, but keep the shape valid
            // by turning it into a block-bodied method).
            return node
                .WithExpressionBody(null)
                .WithSemicolonToken(Token(SyntaxKind.None))
                .WithBody(replacementBody);
        }

        private static bool IsAlreadyReplaced(MethodDeclarationSyntax node)
        {
            if (node.Body is null)
                return false;

            foreach (var trivia in node.Body.DescendantTrivia())
            {
                if ((trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                     || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                    && trivia.ToString().Contains(ReplacementMarker, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private int LineOf(SyntaxNode node)
        {
            try
            {
                return node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            }
            catch
            {
                return 0;
            }
        }

        private int ColumnOf(SyntaxNode node)
        {
            try
            {
                return node.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
            }
            catch
            {
                return 0;
            }
        }
    }

    // ---- body construction ----

    private static BlockSyntax BuildReplacementBody(
        CompatMethodReplacementEntry entry,
        IMethodSymbol symbol,
        BlockSyntax? originalBody)
    {
        var bodyIndent = originalBody is null
            ? "    "
            : GetIndentation(originalBody.GetLeadingTrivia()).ToString();
        var inner = bodyIndent + "    ";

        var template = SubstituteParameterNames(
            entry.BodyTemplate, entry.Parameters, symbol);
        var lines = template
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim('\n')
            .Split('\n');

        // Keep the file's existing line-ending style so the rewritten body does
        // not introduce mixed EOLs.
        var newLine = originalBody is not null
                      && originalBody.ToFullString().Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";

        var builder = new StringBuilder();
        builder.Append('{').Append(newLine);
        builder.Append(inner)
            .Append("// ")
            .Append(ReplacementMarker)
            .Append(": ")
            .Append(entry.Key)
            .Append(newLine);
        foreach (var line in lines)
        {
            if (line.Length > 0)
                builder.Append(inner).Append(line);
            builder.Append(newLine);
        }

        builder.Append(bodyIndent).Append('}');

        var block = (BlockSyntax)ParseStatement(builder.ToString());
        return block
            .WithLeadingTrivia(originalBody?.GetLeadingTrivia() ?? default)
            .WithTrailingTrivia(originalBody?.GetTrailingTrivia() ?? default);
    }

    /// <summary>
    /// Replace each declared placeholder name (whole-identifier match) with the
    /// matched method's actual parameter name, so a parameter rename cannot
    /// produce a body that references undeclared identifiers. Parameters the
    /// ref-out-in pass lifted to a <c>Cs2jByRef</c> holder are referenced
    /// through <c>.Value</c> (the proxy is not the value), including an
    /// already-present <c>name.Value</c> so it never becomes <c>name.Value.Value</c>.
    /// </summary>
    private static string SubstituteParameterNames(
        string template,
        IReadOnlyList<CompatMethodParameter> declaredParameters,
        IMethodSymbol symbol)
    {
        var result = template;
        for (var i = 0; i < declaredParameters.Count; i++)
        {
            var declaredName = declaredParameters[i].Name;
            var actual = symbol.Parameters[i];
            var actualName = actual.Name;

            if (CompatMethodReplacementEntry.TryUnwrapRefHolder(actual.Type, out _, out _))
            {
                result = Regex.Replace(
                    result,
                    @"\b" + Regex.Escape(declaredName) + @"\b(?:\s*\.\s*Value\b)?",
                    _ => actualName + ".Value");
                continue;
            }

            if (string.Equals(declaredName, actualName, StringComparison.Ordinal))
                continue;

            result = Regex.Replace(
                result,
                @"\b" + Regex.Escape(declaredName) + @"\b",
                _ => actualName);
        }

        return result;
    }

    private static SyntaxTriviaList GetIndentation(SyntaxTriviaList leading)
    {
        for (var i = leading.Count - 1; i >= 0; i--)
        {
            var trivia = leading[i];
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                return TriviaList(trivia);

            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                break;
        }

        return TriviaList();
    }

    // ---- helpers ----

    private static bool IsCs2jGeneratedSupportFile(string? filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith("_Cs2j", StringComparison.Ordinal)
               && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ParsesWithoutErrors(string candidate)
    {
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(candidate, ParseOptions);
            return !reparsed.GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }

    private static CompatMethodReplacementResult Rollback(
        string sourceCode,
        CompatMethodReplacementStatistics statistics,
        IReadOnlyList<CompatMethodReplacementDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
}
