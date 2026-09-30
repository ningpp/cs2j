using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.IndexRange;

/// <summary>
/// Public entry point of the C31 index-range preprocessor. Rewrites
/// <c>^</c> (from-end) and <c>..</c> (range) expressions into explicit
/// length-based int indices and <c>(offset, count)</c> calls. Clean files
/// are byte-level preserved; the rewritten output is valid C#.
/// </summary>
public sealed class IndexRangePreprocessor
{
    public IndexRangeResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        IndexRangeOptions? options = null)
    {
        options ??= new IndexRangeOptions();
        var stats = new IndexRangeStatistics { FilesScanned = 1 };
        var diagnostics = new List<IndexRangeDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        var isDirty = IndexRangeSyntaxFacts.IsDirty(root);
        var hasDeclarationSites = semanticModel != null
            && HasIndexRangeDeclarations(root, semanticModel);

        if (!isDirty && !hasDeclarationSites)
        {
            stats.FilesSkippedClean = 1;
            return new IndexRangeResult(
                sourceCode, false, Array.Empty<IndexRangeDiagnostic>(), stats);
        }

        var rewriter = new IndexRangeSyntaxRewriter(
            semanticModel, options, stats, diagnostics, root);
        var pass1Root = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (rewriter.RewriteCount == 0)
        {
            // Dirty but every site is a diagnostic-only site: keep the source
            // bytes untouched; errors fail the run regardless.
            stats.FilesDirtyNoRewrite = 1;
            return new IndexRangeResult(sourceCode, false, diagnostics, stats);
        }

        var pass2Root = TempLifter.Apply(
            pass1Root, rewriter.Pending, rewriter.AnchorAnnotations);

        var residualCount = CountResidual(pass2Root);
        stats.ResidualCount = residualCount;

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(pass2Root, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;

        if (residualCount > 0)
        {
            var severity = options.Strict
                ? IndexRangeSeverity.Error
                : IndexRangeSeverity.Warning;
            diagnostics.Add(new IndexRangeDiagnostic(
                "IR140",
                severity,
                $"{residualCount} residual ^/.. expression(s) remaining after " +
                "index-range preprocessing."));
        }

        return new IndexRangeResult(code, true, diagnostics, stats);
    }

    /// <summary>
    /// Detects System.Index/System.Range typed declarations (parameters,
    /// locals/fields, method/local-function returns, properties) even when the
    /// file contains no <c>^</c>/<c>..</c> syntax, so that such files are not
    /// byte-preserved as clean and their IR120 diagnostics still fire.
    /// A textual prefilter keeps the semantic-model lookup to declaration
    /// types whose name actually mentions Index/Range.
    /// </summary>
    private static bool HasIndexRangeDeclarations(
        SyntaxNode root,
        SemanticModel model)
    {
        foreach (var node in root.DescendantNodes())
        {
            TypeSyntax? type = node switch
            {
                ParameterSyntax parameter => parameter.Type,
                VariableDeclarationSyntax variable
                    when !variable.Type.IsVar => variable.Type,
                MethodDeclarationSyntax method => method.ReturnType,
                LocalFunctionStatementSyntax localFunction
                    => localFunction.ReturnType,
                PropertyDeclarationSyntax property => property.Type,
                _ => null,
            };

            if (type == null)
                continue;

            var text = type.ToString();
            if (!text.Contains("Index") && !text.Contains("Range"))
                continue;

            if (ReceiverKindAnalyzer.IsIndexOrRangeType(
                    model.GetTypeInfo(type).Type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts remaining IndexExpression/RangeExpression nodes that are not
    /// annotated as diagnostic-covered or baseline-skipped.
    /// </summary>
    internal static int CountResidual(SyntaxNode root)
    {
        var total = 0;
        var accounted = 0;
        foreach (var node in root.DescendantNodes())
        {
            if (!IndexRangeSyntaxFacts.IsIndexOrRangeNode(node))
                continue;

            total++;
            if (node.HasAnnotation(IndexRangeSyntaxFacts.CoveredAnnotation)
                || node.HasAnnotation(IndexRangeSyntaxFacts.SkippedAnnotation))
            {
                accounted++;
            }
        }

        return total - accounted;
    }
}
