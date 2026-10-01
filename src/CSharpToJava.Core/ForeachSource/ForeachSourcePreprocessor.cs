using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// C# foreach-source preprocessor public entry point (C16).
/// FS-1 wraps plain foreach over <see cref="string"/> with
/// <c>ToCharArray()</c>; M2 additionally closes residual deconstruction
/// forms. Clean files (no foreach syntax at all) are returned byte-for-byte
/// identical; a null semantic model never throws.
/// </summary>
public sealed class ForeachSourcePreprocessor
{
    public ForeachSourceResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        ForeachSourceOptions? options = null)
    {
        options ??= new ForeachSourceOptions();
        var stats = new ForeachSourceStatistics { FilesScanned = 1 };

        // Use the syntax tree from the semantic model when available so that
        // GetTypeInfo/GetForEachStatementInfo works correctly. Otherwise parse
        // a fresh tree (no semantic info — only the cheap pre-check works).
        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // Cheap pre-check: when no foreach syntax node exists, copy verbatim.
        if (!root.DescendantNodes().OfType<CommonForEachStatementSyntax>().Any())
        {
            stats.FilesSkippedClean = 1;
            return new ForeachSourceResult(
                sourceCode, Changed: false, Array.Empty<ForeachSourceDiagnostic>(), stats);
        }

        var rewriter = new ForeachSourceRewriter(
            semanticModel, options, stats, new List<ForeachSourceDiagnostic>());
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            stats.FilesSkippedClean = 1;
            return new ForeachSourceResult(
                sourceCode, Changed: false, rewriter.Diagnostics, stats);
        }

        // Format to normalize whitespace/trivia on generated nodes.
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(rewritten, workspace);
        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;
        return new ForeachSourceResult(
            code, Changed: true, rewriter.Diagnostics, stats);
    }
}
