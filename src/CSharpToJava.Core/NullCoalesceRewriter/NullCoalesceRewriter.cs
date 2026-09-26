using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <summary>
/// C# null-coalesce preprocessor public entry point. Rewrites ?? and ??=
/// operators into equivalent explicit ternary / if-assignment forms.
/// Clean files are byte-level preserved. Rewritten output is valid C#.
/// </summary>
public sealed class NullCoalesceRewriter
{
    public NullCoalesceResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        NullCoalesceOptions? options = null)
    {
        options ??= new NullCoalesceOptions();
        var stats = new NullCoalesceStatistics { FilesScanned = 1 };
        var diagnostics = new List<NullCoalesceDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // Dirty check: no ?? or ??= → byte-level copy
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new NullCoalesceResult(sourceCode, false, diagnostics, stats);
        }

        // Pass 1: rewrite expressions, collect temp declarations
        var rewriter = new NullCoalesceSyntaxRewriter(
            semanticModel, options, stats, diagnostics);
        var pass1Root = (CompilationUnitSyntax)rewriter.Visit(root)!;

        // Pass 2: lift temp declarations to before their anchor statements
        var lifter = new NullCoalesceStatementLifter(rewriter.PendingDeclarations);
        var pass2Root = (CompilationUnitSyntax)lifter.Visit(pass1Root)!;

        // Format
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(pass2Root, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;

        // Post-check: residual ?? / ??=
        if (ContainsResidualNullCoalesce(code))
        {
            stats.ResidualCount = CountResidual(code);
            var severity = options.Strict
                ? NullCoalesceSeverity.Error
                : NullCoalesceSeverity.Warning;
            diagnostics.Add(new NullCoalesceDiagnostic(
                severity,
                $"{stats.ResidualCount} residual ??/??= operator(s) remaining after preprocessing."));
        }

        return new NullCoalesceResult(code, true, diagnostics, stats);
    }

    /// <summary>Dirty check: syntax tree contains any CoalesceExpression or CoalesceAssignmentExpression.</summary>
    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
            || n is AssignmentExpressionSyntax a
                && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));

    /// <summary>Post-check: rewritten output still contains ?? or ??=.</summary>
    internal static bool ContainsResidualNullCoalesce(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        return IsDirty(root);
    }

    private static int CountResidual(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().Count(n =>
            n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
            || n is AssignmentExpressionSyntax a
                && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));
    }
}
