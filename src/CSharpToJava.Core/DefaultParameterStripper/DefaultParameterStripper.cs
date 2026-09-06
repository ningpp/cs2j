using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed class DefaultParameterStripper
{
    private readonly DefaultParameterCatalog _catalog = new();

    /// <summary>Pass 1: scan a syntax tree and register existing signatures.</summary>
    public void Collect(SyntaxTree tree, SemanticModel model)
        => _catalog.Collect(tree, model);

    /// <summary>Pass 2: strip defaults + generate overloads.</summary>
    public DefaultParameterStripperResult Strip(
        string sourceCode,
        SemanticModel? model = null,
        DefaultParameterStripperOptions? options = null)
    {
        options ??= new DefaultParameterStripperOptions();
        var stats = new DefaultParameterStripperStatistics { FilesScanned = 1 };
        var diagnostics = new List<DefaultParameterStripDiagnostic>();

        var tree = model != null
            ? model.SyntaxTree
            : CSharpSyntaxTree.ParseText(sourceCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // Dirty check: any ParameterSyntax with Default in managed context
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new DefaultParameterStripperResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new DefaultParameterDeclarationRewriter(
            model, options, stats, diagnostics, _catalog);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            stats.FilesSkippedClean = 1;
            return new DefaultParameterStripperResult(sourceCode, false, diagnostics, stats);
        }

        using var workspace = new AdhocWorkspace();
        newRoot = (CompilationUnitSyntax)Formatter.Format(newRoot, workspace);

        stats.FilesTransformed = 1;
        return new DefaultParameterStripperResult(
            newRoot.ToFullString(), true, diagnostics, stats);
    }

    /// <summary>
    /// Dirty check: any Method/Constructor/LocalFunction/Delegate with a
    /// parameter that has Default != null. Delegates are included so the
    /// rewriter can count them as SkippedUnsupported. Excludes indexer/operator.
    /// </summary>
    private static bool IsDirty(CompilationUnitSyntax root)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.ParameterList.Parameters.Any(p => p.Default != null))
                return true;
        }
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            if (ctor.ParameterList.Parameters.Any(p => p.Default != null))
                return true;
        }
        foreach (var local in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (local.ParameterList != null && local.ParameterList.Parameters.Any(p => p.Default != null))
                return true;
        }
        foreach (var del in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            if (del.ParameterList != null && del.ParameterList.Parameters.Any(p => p.Default != null))
                return true;
        }
        return false;
    }
}
