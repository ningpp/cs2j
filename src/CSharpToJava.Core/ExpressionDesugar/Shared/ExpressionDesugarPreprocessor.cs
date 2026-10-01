using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Public entry point of the <c>expression-desugar</c> family.
/// M0 behavior (plan §3 M0):
/// <list type="bullet">
/// <item>clean files (no rule's dirty gate matches) are byte-level preserved
/// (Changed=false, the adapter performs the verbatim copy incl. BOM);</item>
/// <item>dirty files run every matching rule's Analyze for statistics and
/// diagnostics, but no rewrite is emitted yet — the source is returned
/// unchanged with Changed=false ("pass skeleton; no rewrite");</item>
/// <item>without a semantic model a dirty file is safety-skipped with a
/// warning diagnostic instead of crashing.</item>
/// </list>
/// Later milestones add plan-driven rewriting, <see cref="ExpressionDesugarTempLifter.Apply"/>
/// and formatting; this entry's result shape already matches that pipeline.
/// </summary>
public sealed class ExpressionDesugarPreprocessor
{
    public ExpressionDesugarResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        ExpressionDesugarOptions? options = null)
    {
        options ??= new ExpressionDesugarOptions();
        var stats = new ExpressionDesugarStatistics { FilesScanned = 1 };
        var diagnostics = new List<ExpressionDesugarDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        var activeRules = ExpressionDesugarRuleSet.Rules
            .Where(rule => rule.IsPotentiallyDirty(root))
            .ToList();

        if (activeRules.Count == 0)
        {
            stats.FilesSkippedClean = 1;
            return new ExpressionDesugarResult(
                sourceCode, false, Array.Empty<ExpressionDesugarDiagnostic>(), stats);
        }

        if (semanticModel == null)
        {
            // The family dispatches on SemanticModel ConvertedType; without a
            // model every rewrite decision would be a guess. Safe skip with an
            // explicit diagnostic (non-strict Warning) and unchanged bytes.
            stats.FilesDirtyNoRewrite = 1;
            diagnostics.Add(new ExpressionDesugarDiagnostic(
                ExpressionDesugarSeverity.Warning,
                CollectionExprDiagnosticCodes.CS2J0571,
                "A semantic model is required to classify collection-expression " +
                "sites; the file was left unchanged."));
            return new ExpressionDesugarResult(
                sourceCode, false, diagnostics, stats);
        }

        var context = new RuleAnalysisContext
        {
            Root = root,
            SemanticModel = semanticModel,
            Options = options,
            Statistics = stats,
            Diagnostics = diagnostics,
        };

        foreach (var rule in activeRules)
        {
            rule.Analyze(context);
        }

        // M0: analysis only. No rewriter, no TempLifter.Apply, no Formatter —
        // dirty files keep their source bytes (Changed=false).
        stats.FilesDirtyNoRewrite = 1;
        return new ExpressionDesugarResult(sourceCode, false, diagnostics, stats);
    }
}
