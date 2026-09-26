using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.ForeachDeconstruction;

/// <summary>
/// C# deconstruction foreach preprocessor public entry point.
/// Lowers deconstruction foreach over KeyValuePair collections into plain
/// foreach + explicit local declarations. Clean files (no lowerable
/// deconstruction foreach) are returned byte-for-byte identical.
/// </summary>
public sealed class ForeachDeconstructionPreprocessor
{
    public ForeachDeconstructionResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        ForeachDeconstructionOptions? options = null)
    {
        options ??= new ForeachDeconstructionOptions();
        var stats = new ForeachDeconstructionStatistics { FilesScanned = 1 };

        // Use the syntax tree from the semantic model when available so that
        // SemanticModel.GetForEachStatementInfo works correctly. Otherwise
        // parse a fresh tree (no semantic info — only pre-check works).
        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // Cheap pre-check: if no ForEachVariableStatementSyntax at all, return original.
        if (!root.DescendantNodes().OfType<ForEachVariableStatementSyntax>().Any())
        {
            stats.FilesSkippedClean = 1;
            return new ForeachDeconstructionResult(
                sourceCode, Changed: false, Array.Empty<ForeachDeconstructionDiagnostic>(), stats);
        }

        var rewriter = new ForeachDeconstructionRewriter(
            semanticModel, options, stats, new List<ForeachDeconstructionDiagnostic>());
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            stats.FilesSkippedClean = 1;
            return new ForeachDeconstructionResult(
                sourceCode, Changed: false, rewriter.Diagnostics, stats);
        }

        // Format to normalize whitespace/trivia on generated nodes.
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(rewritten, workspace);
        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;
        return new ForeachDeconstructionResult(
            code, Changed: true, rewriter.Diagnostics, stats);
    }
}
