using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// Public per-file entry for the numeric-boundary pass (C15, M1). Mirrors
/// <see cref="UnsignedTypeRewriter.UnsignedTypeRewriter"/>: one semantic-model
/// traversal per file, span formatting with reparsing validation, byte-identical
/// passthrough for clean files.
/// </summary>
public sealed class NumericBoundaryPreprocessor
{
    public NumericBoundaryFileResult Rewrite(
        string sourceCode,
        SemanticModel? semanticModel,
        NumericBoundaryOptions? options = null)
    {
        options ??= new NumericBoundaryOptions();
        var stats = new NumericBoundaryStatistics { FilesScanned = 1 };
        var diagnostics = new List<NumericBoundaryDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new NumericBoundaryFileResult(sourceCode, false, diagnostics, stats);
        }

        var projectDefaultChecked = NumericBoundaryAnalyzer.ResolveProjectDefaultChecked(
            options.SourceFilePath,
            options.CheckForOverflowUnderflowByProjectDirectory) ?? false;

        var analyzer = new NumericBoundaryAnalyzer(semanticModel, projectDefaultChecked);
        var rewriter = new CheckedExprRewriter(analyzer, stats);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        // NB-13 residual gate (M1 scope: unchecked must be gone everywhere in user
        // code; checked nodes may remain until M2/M3). Runs on the rewritten tree.
        stats.ResidualUncheckedNodes = CountUncheckedNodes(newRoot);
        stats.ResidualCheckedNodes = CountCheckedNodes(newRoot);
        if (stats.ResidualUncheckedNodes > 0)
        {
            var severity = options.Strict
                ? NumericBoundarySeverity.Error
                : NumericBoundarySeverity.Warning;
            diagnostics.Add(new NumericBoundaryDiagnostic(
                severity,
                PreprocessDiagnosticCodes.NumericBoundaryResidualNode,
                $"{stats.ResidualUncheckedNodes} residual unchecked node(s) remain after numeric-boundary " +
                "preprocessing; user code must not keep unchecked expressions/blocks."));
            if (severity == NumericBoundarySeverity.Error)
            {
                stats.ErrorsByReason[PreprocessDiagnosticCodes.NumericBoundaryResidualNode] =
                    stats.ResidualUncheckedNodes;
            }
        }

        if (!rewriter.MadeChanges)
        {
            // M1 common case: file contains checked-only sites that M1 preserves.
            stats.FilesDirtyNoRewrite = 1;
            return new NumericBoundaryFileResult(sourceCode, false, diagnostics, stats);
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            stats.FilesDirtyNoRewrite = 1;
            diagnostics.Add(new NumericBoundaryDiagnostic(
                NumericBoundarySeverity.Error,
                PreprocessDiagnosticCodes.NumericBoundaryBuildFailed,
                "numeric-boundary rewrite produced invalid C# syntax; the original file was preserved."));
            stats.ErrorsByReason[PreprocessDiagnosticCodes.NumericBoundaryBuildFailed] = 1;
            return new NumericBoundaryFileResult(sourceCode, false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new NumericBoundaryFileResult(outputCode, true, diagnostics, stats);
    }

    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(static node =>
            node is CheckedExpressionSyntax or CheckedStatementSyntax);

    private static int CountUncheckedNodes(SyntaxNode root)
        => root.DescendantNodes().Count(static node =>
            (node is CheckedExpressionSyntax expression
                && expression.IsKind(SyntaxKind.UncheckedExpression))
            || (node is CheckedStatementSyntax statement
                && statement.IsKind(SyntaxKind.UncheckedStatement)));

    private static int CountCheckedNodes(SyntaxNode root)
        => root.DescendantNodes().Count(static node =>
            (node is CheckedExpressionSyntax expression
                && expression.IsKind(SyntaxKind.CheckedExpression))
            || (node is CheckedStatementSyntax statement
                && statement.IsKind(SyntaxKind.CheckedStatement)));

    /// <summary>
    /// Same three-candidate strategy as the unsigned rewriter: format only the
    /// rewritten spans, then the whole file, then raw text; every candidate must
    /// reparse without errors.
    /// </summary>
    private static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        using var workspace = new AdhocWorkspace();
        var candidates = new List<string>();

        try
        {
            var spans = GetRewrittenSpans(root);
            if (spans.Count > 0)
            {
                candidates.Add(Formatter.Format(root, spans, workspace).ToFullString());
            }
        }
        catch
        {
            // Local formatting failure is non-fatal.
        }

        try
        {
            candidates.Add(Formatter.Format(root, workspace).ToFullString());
        }
        catch
        {
            // Whole-file formatting failure is non-fatal.
        }

        candidates.Add(root.ToFullString());

        foreach (var candidate in candidates)
        {
            var reparsed = CSharpSyntaxTree.ParseText(candidate);
            if (!reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<Microsoft.CodeAnalysis.Text.TextSpan> GetRewrittenSpans(SyntaxNode root)
    {
        var annotated = root
            .GetAnnotatedNodes(NumericBoundarySyntaxFacts.RewriteAnnotationKind)
            .Where(n => n is ExpressionSyntax or StatementSyntax)
            .ToList();
        if (annotated.Count == 0)
        {
            return new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        }

        var annotatedSet = new HashSet<SyntaxNode>(annotated);
        return annotated
            .Where(n => !n.Ancestors().Any(annotatedSet.Contains))
            .Select(n => n.Span)
            .Distinct()
            .OrderBy(s => s.Start)
            .ToList();
    }
}
