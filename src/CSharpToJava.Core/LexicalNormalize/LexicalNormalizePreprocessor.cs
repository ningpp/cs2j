using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.LexicalNormalize.Literals;

namespace CSharpToJava.Core.LexicalNormalize;

/// <summary>
/// Pure-syntax, idempotent lexical normalization. Parses the source once; guard
/// diagnostics (LN-2/3/4) gate every rewrite, and a verification failure rolls
/// the file back. No CSharpCompilation / SemanticModel is ever created.
/// </summary>
public sealed class LexicalNormalizePreprocessor
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    public LexicalNormalizeResult Normalize(
        string sourceCode,
        LexicalNormalizeOptions? options = null)
    {
        options ??= new LexicalNormalizeOptions();

        var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions);
        var root = tree.GetCompilationUnitRoot();

        var scan = DirectiveScan.Run(root, options);
        var diagnostics = scan.Diagnostics;
        var stats = scan.Stats;
        stats.FilesScanned = 1;

        // Roslyn parse errors surfaced with the compiler id (CS1xxx); #error is
        // already covered with CS2J5511 by DirectiveScan.
        foreach (var diagnostic in tree.GetDiagnostics()
                     .Where(d => d.Severity == DiagnosticSeverity.Error
                         && d.Id != "CS1029"))
        {
            stats.AddError(diagnostic.Id);
            diagnostics.Add(new LexicalNormalizeDiagnostic(
                LexicalNormalizeSeverity.Error,
                diagnostic.Id,
                $"[LN] parse error: {diagnostic.GetMessage()}",
                diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1));
        }

        var effective = diagnostics
            .Select(d => EffectiveSeverity(d, options.Strict))
            .ToList();
        var hasError = effective.Any(s => s == LexicalNormalizeSeverity.Error);

        if (hasError)
        {
            stats.FilesDirtyNoRewrite = 1;
            return new LexicalNormalizeResult(
                sourceCode,
                Changed: false,
                Materialize(diagnostics, options),
                stats.Build(),
                scan.GlobalUsings);
        }

        var working = root;

        if (options.StripLineDirectives)
        {
            var lineRewriter = new LineDirectiveRewriter();
            working = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)lineRewriter.Visit(working);
            stats.LineDirectivesRemoved += lineRewriter.LineDirectivesRemoved;
        }

        if (options.NormalizeFileScopedNamespaces)
        {
            var namespaceRewriter = new NamespaceRewriter();
            working = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)namespaceRewriter.Visit(working);
            stats.FileScopedNamespacesNormalized += namespaceRewriter.NormalizedCount;
        }

        var anyLiteralRule = options.NormalizeRawLiterals
            || options.NormalizeUtf8Literals
            || options.NormalizeVerbatimLiterals
            || options.NormalizeEscEscapes;

        LiteralRewriteRecorder? literalRecorder = null;
        if (anyLiteralRule)
        {
            var literalRewriter = new LiteralRewriter(options);
            working = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)
                literalRewriter.Visit(working);
            literalRecorder = literalRewriter.Recorder;
            literalRecorder.CopyTo(stats);
            diagnostics.AddRange(literalRecorder.BuildAggregatedInfo());
        }

        var output = UnifyLineEndings(sourceCode, working.ToFullString());
        var changed = !string.Equals(sourceCode, output, StringComparison.Ordinal);

        if (!changed)
        {
            stats.FilesSkippedClean = 1;
            return new LexicalNormalizeResult(
                sourceCode,
                Changed: false,
                Materialize(diagnostics, options),
                stats.Build(),
                scan.GlobalUsings);
        }

        var literalRewrote = literalRecorder is not null && literalRecorder.TotalRewrites > 0;

        if (!LexicalNormalizeVerifier.Verify(
                output, root, options, literalRewrote, out var failure)
            && failure != null)
        {
            stats.AddError(failure.Code);
            diagnostics.Add(failure);
            stats.FilesDirtyNoRewrite = 1;
            return new LexicalNormalizeResult(
                sourceCode,
                Changed: false,
                Materialize(diagnostics, options),
                stats.Build(),
                scan.GlobalUsings);
        }

        if (literalRecorder is not null)
        {
            var literalOutcome = LiteralNormalizationVerifier.Verify(
                output, options, literalRecorder);
            diagnostics.AddRange(literalOutcome.Diagnostics);

            if (literalOutcome.Fatal)
            {
                foreach (var error in literalOutcome.Diagnostics
                             .Where(d => d.Severity == LexicalNormalizeSeverity.Error))
                {
                    stats.AddError(error.Code);
                }

                stats.FilesDirtyNoRewrite = 1;
                return new LexicalNormalizeResult(
                    sourceCode,
                    Changed: false,
                    Materialize(diagnostics, options),
                    stats.Build(),
                    scan.GlobalUsings);
            }
        }

        stats.FilesTransformed = 1;
        return new LexicalNormalizeResult(
            output,
            Changed: true,
            Materialize(diagnostics, options),
            stats.Build(),
            scan.GlobalUsings);
    }

    /// <summary>
    /// Unifies the rewritten text to the input's dominant line ending. The
    /// Formatter introduces <see cref="Environment.NewLine"/> trivia, which would
    /// otherwise mix endings; files without any newline default to LF.
    /// </summary>
    private static string UnifyLineEndings(string original, string output)
    {
        if (output.Length == 0 || (!output.Contains('\r') && !output.Contains('\n')))
        {
            return output;
        }

        var crlf = 0;
        var bareLf = 0;
        var bareCr = 0;
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] == '\r')
            {
                if (i + 1 < original.Length && original[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    bareCr++;
                }
            }
            else if (original[i] == '\n')
            {
                bareLf++;
            }
        }

        if (crlf == 0 && bareLf == 0 && bareCr == 0)
        {
            return output.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        if (crlf >= bareLf && crlf >= bareCr)
        {
            return output.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        }

        return output.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private static LexicalNormalizeSeverity EffectiveSeverity(
        LexicalNormalizeDiagnostic diagnostic,
        bool strict)
        => strict && diagnostic.Severity == LexicalNormalizeSeverity.Warning
            ? LexicalNormalizeSeverity.Error
            : diagnostic.Severity;

    private static List<LexicalNormalizeDiagnostic> Materialize(
        List<LexicalNormalizeDiagnostic> diagnostics,
        LexicalNormalizeOptions options)
        => diagnostics
            .Where(d => options.ReportInfo || d.Severity != LexicalNormalizeSeverity.Info)
            .Select(d => d with
            {
                Severity = EffectiveSeverity(d, options.Strict),
            })
            .ToList();
}
