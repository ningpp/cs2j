using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DelegatePreprocessor;

/// <summary>
/// C# delegate preprocessor public entry point. Rewrites delegate + / - / += / -=
/// to CsPrelude.Combine / CsPrelude.Remove, and anonymous methods to lambdas.
/// Clean files are byte-level preserved. Rewritten output is valid C#.
/// </summary>
public sealed class DelegatePreprocessor
{
    public DelegatePreprocessorResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        DelegatePreprocessorOptions? options = null)
    {
        options ??= new DelegatePreprocessorOptions();
        var stats = new DelegatePreprocessorStatistics { FilesScanned = 1 };
        var diagnostics = new List<DelegatePreprocessorDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // Dirty check: no + / - / += / -= / delegate anonymous → byte-level copy
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new DelegatePreprocessorResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new DelegateSyntaxRewriter(
            semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            // Dirty but nothing to rewrite (e.g. numeric arithmetic only)
            stats.FilesSkippedClean = 1;
            return new DelegatePreprocessorResult(sourceCode, false, diagnostics, stats);
        }

        // No Formatter.Format — use ToFullString() to minimize trivia disruption
        var code = newRoot.ToFullString();
        stats.FilesTransformed = 1;

        // Post-check: residual anonymous methods
        var checkRoot = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        if (checkRoot.DescendantNodes().Any(n => n is AnonymousMethodExpressionSyntax))
        {
            stats.ResidualCount = checkRoot.DescendantNodes()
                .Count(n => n is AnonymousMethodExpressionSyntax);
            var severity = options.Strict
                ? DelegatePreprocessorSeverity.Error
                : DelegatePreprocessorSeverity.Warning;
            diagnostics.Add(new DelegatePreprocessorDiagnostic(
                severity,
                $"{stats.ResidualCount} residual anonymous method(s) remaining after preprocessing."));
        }

        return new DelegatePreprocessorResult(code, true, diagnostics, stats);
    }

    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            n is AnonymousMethodExpressionSyntax
            || (n is BinaryExpressionSyntax b &&
                (b.IsKind(SyntaxKind.AddExpression) || b.IsKind(SyntaxKind.SubtractExpression)))
            || (n is AssignmentExpressionSyntax a &&
                (a.IsKind(SyntaxKind.AddAssignmentExpression)
                 || a.IsKind(SyntaxKind.SubtractAssignmentExpression))));
}
