using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// Public per-file entry for the numeric-boundary pass (C15). Mirrors
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

        var projectDefaultChecked = NumericBoundaryAnalyzer.ResolveProjectDefaultChecked(
            options.SourceFilePath,
            options.CheckForOverflowUnderflowByProjectDirectory) ?? false;

        var c15Dirty = IsDirty(root, projectDefaultChecked);

        // EnableDecimalOps=true keeps the pipeline alive on keyword-free files so
        // decimal sites (which live outside checked scopes too) get normalized;
        // while false the gate and all statistics stay byte-identical to C15.
        if (!options.EnableDecimalOps && !c15Dirty)
        {
            stats.FilesSkippedClean = 1;
            return new NumericBoundaryFileResult(sourceCode, false, diagnostics, stats);
        }

        var analyzer = new NumericBoundaryAnalyzer(semanticModel, projectDefaultChecked);
        var rewriter = new CheckedExprRewriter(analyzer, stats, diagnostics, options.EnableDecimalOps);
        var visitedRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var newRoot = (CompilationUnitSyntax)rewriter.ApplyHoists(visitedRoot);

        // NB-13 residual gate on the rewritten tree (M2/M3: both checked and
        // unchecked keyword nodes must be gone from user code).
        ApplyResidualGate(newRoot, options, stats, diagnostics);

        var decimalMadeChanges = false;
        var downgradeMadeChanges = false;

        // ── C34 decimal-ops pipeline (dormant unless explicitly enabled) ──
        if (options.EnableDecimalOps && semanticModel != null)
        {
            // C15 produced a new tree: rebuild a semantic model before C34 can
            // classify operator bindings on the post-hoist intermediate form.
            // The model is bound to a freshly parsed tree, so every subsequent
            // operation uses that tree's own root.
            var postCheckedModel = RebuildSemanticModel(semanticModel, newRoot);
            if (postCheckedModel != null)
            {
                var reboundRoot = (CompilationUnitSyntax)postCheckedModel.SyntaxTree.GetRoot();
                var downgradeTargets = DecimalOpAnalyzer.PlanConstDowngrades(postCheckedModel, reboundRoot);

                var decimalRewriter = new DecimalOpRewriter(
                    postCheckedModel, options, stats.DecimalOps, diagnostics, downgradeTargets);
                var decimalRoot = (CompilationUnitSyntax)decimalRewriter.Visit(reboundRoot)!;
                decimalRoot = (CompilationUnitSyntax)decimalRewriter.ApplyHoists(decimalRoot);

                var downgrader = new DecimalOpConstDowngrader(stats.DecimalOps);
                decimalRoot = (CompilationUnitSyntax)downgrader.Visit(decimalRoot)!;
                decimalMadeChanges = decimalRewriter.MadeChanges;
                downgradeMadeChanges = downgrader.MadeChanges;

                // DC-13: rescan the final output tree for surviving built-in
                // decimal operators (lifted/user-operator sites whitelisted).
                var finalModel = RebuildSemanticModel(postCheckedModel, decimalRoot);
                if (finalModel != null)
                {
                    var finalRoot = (CompilationUnitSyntax)finalModel.SyntaxTree.GetRoot();
                    DecimalOpRewriter.ApplyResidualGate(
                        finalModel, finalRoot, options, stats.DecimalOps, diagnostics);
                }

                newRoot = decimalRoot;
            }
        }

        // Mirror C34 error codes into the C15 error buckets so adapters that only
        // merge ErrorsByReason still see them.
        foreach (var errorCode in diagnostics
                     .Where(d => d.Severity == NumericBoundarySeverity.Error && d.Code.StartsWith("CS2J536", StringComparison.Ordinal))
                     .Select(d => d.Code))
        {
            stats.ErrorsByReason[errorCode] = stats.ErrorsByReason.GetValueOrDefault(errorCode) + 1;
        }

        // NB-12: any error diagnostic rolls the whole file back to the original
        // bytes — a half-rewritten file must never reach the intermediate tree.
        if (diagnostics.Any(d => d.Severity == NumericBoundarySeverity.Error))
        {
            stats.FilesDirtyNoRewrite = 1;
            return new NumericBoundaryFileResult(sourceCode, false, diagnostics, stats);
        }

        if (!rewriter.MadeChanges && !decimalMadeChanges && !downgradeMadeChanges)
        {
            // Nothing rewrote. A keyword-/checked-dirty input counts as dirty;
            // a decimal-enabled clean pass counts as skipped-clean.
            if (c15Dirty)
            {
                stats.FilesDirtyNoRewrite = 1;
            }
            else
            {
                stats.FilesSkippedClean = 1;
            }

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

    /// <summary>
    /// Rebind a freshly produced tree by replacing the source tree inside the
    /// previous compilation. Returns null when the new text fails to parse.
    /// </summary>
    private static SemanticModel? RebuildSemanticModel(SemanticModel previousModel, SyntaxNode newRoot)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(newRoot.ToFullString());
        var oldTree = (CSharpSyntaxTree)previousModel.SyntaxTree;
        var newTree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(text, oldTree.Options);
        if (newTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return null;
        }

        var compilation = previousModel.Compilation.ReplaceSyntaxTree(previousModel.SyntaxTree, newTree);
        return compilation.GetSemanticModel(newTree);
    }

    internal static bool IsDirty(CompilationUnitSyntax root, bool projectDefaultChecked)
    {
        if (projectDefaultChecked)
        {
            // A /checked project rewrites keyword-free files too (every integer
            // arithmetic site is checked by default).
            return true;
        }

        return root.DescendantNodes().Any(static node =>
            node is CheckedExpressionSyntax or CheckedStatementSyntax);
    }

    /// <summary>
    /// NB-13: scan the output tree for surviving checked/unchecked nodes.
    /// Unchecked survivors are always an error. Surviving checked EXPRESSION
    /// shells are exactly the user-defined <c>op_Checked*</c> vouchers kept for
    /// member-desugar (their count is tracked during the traversal); they are
    /// exempt only while member-desugar co-runs (decision 4 / open question 6).
    /// Any mismatch (or a surviving checked BLOCK) is CS2J5327 (strict → Error).
    /// </summary>
    private static void ApplyResidualGate(
        SyntaxNode root,
        NumericBoundaryOptions options,
        NumericBoundaryStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics)
    {
        var memberDesugarCoRunning =
            options.EnabledPassIds?.Contains(NumericBoundaryOptions.MemberDesugarPassId) == true;

        var residualUnchecked = 0;
        var residualCheckedExpressions = 0;
        var residualCheckedBlocks = 0;

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case CheckedExpressionSyntax expression:
                    if (expression.IsKind(SyntaxKind.UncheckedExpression))
                    {
                        residualUnchecked++;
                    }
                    else
                    {
                        residualCheckedExpressions++;
                    }

                    break;
                case CheckedStatementSyntax statement:
                    if (statement.IsKind(SyntaxKind.UncheckedStatement))
                    {
                        residualUnchecked++;
                    }
                    else
                    {
                        residualCheckedBlocks++;
                    }

                    break;
            }
        }

        stats.ResidualUncheckedNodes = residualUnchecked;
        stats.ResidualCheckedNodes = residualCheckedExpressions + residualCheckedBlocks;

        // Every surviving checked expression must be one kept voucher.
        var unexpectedChecked = residualCheckedBlocks
            + Math.Max(0, residualCheckedExpressions - stats.CheckedUserOpShellsDeferred);
        var voucherChecked = Math.Min(residualCheckedExpressions, stats.CheckedUserOpShellsDeferred);

        void AddResidual(int count, string message)
        {
            if (count == 0)
            {
                return;
            }

            var severity = options.Strict
                ? NumericBoundarySeverity.Error
                : NumericBoundarySeverity.Warning;
            diagnostics.Add(new NumericBoundaryDiagnostic(
                severity, PreprocessDiagnosticCodes.NumericBoundaryResidualNode, message));
            if (severity == NumericBoundarySeverity.Error)
            {
                stats.ErrorsByReason[PreprocessDiagnosticCodes.NumericBoundaryResidualNode] =
                    stats.ErrorsByReason.GetValueOrDefault(PreprocessDiagnosticCodes.NumericBoundaryResidualNode) + count;
            }
        }

        if (residualUnchecked > 0)
        {
            AddResidual(
                residualUnchecked,
                $"{residualUnchecked} residual unchecked node(s) remain after numeric-boundary " +
                "preprocessing; user code must not keep unchecked expressions/blocks.");
        }

        if (voucherChecked > 0 && !memberDesugarCoRunning)
        {
            AddResidual(
                voucherChecked,
                $"{voucherChecked} checked expression shell(s) wrapping user-defined checked operators " +
                "(op_Checked*) remain, but the member-desugar pass is not enabled in this chain; " +
                "enable member-desugar alongside numeric-boundary.");
        }

        if (unexpectedChecked > 0)
        {
            AddResidual(
                unexpectedChecked,
                $"{unexpectedChecked} residual checked node(s) remain after numeric-boundary preprocessing; " +
                "user code must not keep checked expressions/blocks.");
        }
    }

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
