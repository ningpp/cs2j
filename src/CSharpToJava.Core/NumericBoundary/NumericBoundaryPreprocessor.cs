using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;
using CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;
using CSharpToJava.Core.NumericBoundary.Rules.Nullable;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// Public per-file entry for the numeric-boundary family (C15 anchor; C34 and
/// C17 co-located as family rules). Mirrors
/// <see cref="UnsignedTypeRewriter.UnsignedTypeRewriter"/>: one semantic-model
/// traversal per file, span formatting with reparsing validation, byte-identical
/// passthrough for clean files.
/// <para>
/// 2026-10-01 (C17 M0): the file pipeline is rule-driven
/// (<see cref="INumericBoundaryRule"/>, explicit ordered list, no reflection):
/// C17 IncrementTargets(Order=10) runs first so its expansions are seen by the
/// C15 checked sweep (Order=20); C34 DecimalOps runs last (Order=30). Tree
/// changes between rules trigger a semantic-model rebind through the shared
/// context (design §4.5 implementation correction / decision 17).
/// 2026-10-02 (C22 M0): NullableLift joins at Order=15 (between C17 and C15;
/// the design's Order=30 was a pre-C15 assumption) — guard expansion precedes
/// the C15/C34 sweeps so checked helpers and decimal intake land inside guards.
/// </para>
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

        // Family-level early gate: keep the pipeline alive when any family rule
        // is enabled — EnableDecimalOps (C34), EnableIncrementTargets (C17) and
        // EnableNullable (C22) all own sites outside checked scopes, so
        // keyword-free files must still be traversed. While every rule is
        // dormant the gate and statistics stay byte-identical to C15.
        if (!c15Dirty && !options.EnableDecimalOps && !options.EnableIncrementTargets
            && !options.EnableNullable)
        {
            stats.FilesSkippedClean = 1;
            return new NumericBoundaryFileResult(sourceCode, false, diagnostics, stats);
        }

        var context = new NumericBoundaryRuleContext(
            root, semanticModel, projectDefaultChecked, options, stats, diagnostics);

        // Explicit ordered rule list (no reflection discovery); execution is
        // Order ascending via OrderBy. Written in execution order with the
        // 2026-10-02 C22 correction noted:
        //   C17 IncrementTargets (10): site expansion
        //   C22 NullableLift    (15): guard expansion BEFORE the sweeps so the
        //                            C15 post-sweep inserts checked helpers
        //                            inside guards (decision 18) and C34 takes
        //                            decimal inner ops (decision 19)
        //   C15 CheckedArithmetic (20): checked post-sweep
        //   C34 DecimalOps      (30): decimal intake
        var rules = new INumericBoundaryRule[]
        {
            new IncrementTargetRule(),
            new NullableLiftRule(),
            new CheckedArithmeticRule(),
            new DecimalOpsRule(),
        };

        var finalRoot = root;
        var anyMadeChanges = false;

        foreach (var rule in rules.OrderBy(rule => rule.Order))
        {
            if (!rule.IsEnabled(options))
            {
                continue;
            }

            var result = rule.Execute(context);
            finalRoot = result.Root;
            anyMadeChanges |= result.MadeChanges;

            // A rule changed the tree: rebind before the next rule classifies
            // bindings on the intermediate form. A failed reparse can never yield
            // a valid output, so record the family build-failed error and let the
            // whole-file rollback path below preserve the original bytes.
            if (result.MadeChanges && context.SemanticModel != null
                && context.RebindModel(result.Root) == null)
            {
                diagnostics.Add(new NumericBoundaryDiagnostic(
                    NumericBoundarySeverity.Error,
                    PreprocessDiagnosticCodes.NumericBoundaryBuildFailed,
                    $"numeric-boundary rule '{rule.Id}' produced a tree that fails to reparse while " +
                    "rebinding the semantic model; the original file was preserved " +
                    $"({PreprocessDiagnosticCodes.NumericBoundaryBuildFailed})."));
                stats.ErrorsByReason[PreprocessDiagnosticCodes.NumericBoundaryBuildFailed] = 1;
                break;
            }
        }

        // NB-12: any error diagnostic rolls the whole file back to the original
        // bytes — a half-rewritten file must never reach the intermediate tree.
        if (diagnostics.Any(d => d.Severity == NumericBoundarySeverity.Error))
        {
            stats.FilesDirtyNoRewrite = 1;
            return new NumericBoundaryFileResult(sourceCode, false, diagnostics, stats);
        }

        if (!anyMadeChanges)
        {
            // Nothing rewrote. A keyword-/checked-dirty input counts as dirty;
            // a rule-enabled clean pass counts as skipped-clean.
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

        var outputCode = FormatAndValidate(finalRoot);
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
