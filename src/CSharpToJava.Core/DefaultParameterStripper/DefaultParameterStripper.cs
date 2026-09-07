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

        // Always run the rewriter — it handles both declaration stripping
        // and call-site rewriting (fixing named arguments that skip default
        // parameters). The IsDirty check only skips files with no defaults,
        // but call sites in such files may still need rewriting.
        var hasDefaults = IsDirty(root);

        var rewriter = new DefaultParameterDeclarationRewriter(
            model, options, stats, diagnostics, _catalog, hasDefaults);
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
    /// Dirty check: any callable declaration with a parameter that has
    /// <c>Default != null</c>. Covers methods, constructors, local functions,
    /// delegates and record/class/struct primary constructors. Indexers and
    /// operators are excluded — they can never declare defaults.
    /// </summary>
    private static bool IsDirty(CompilationUnitSyntax root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax method when HasDefault(method.ParameterList):
                    return true;
                case ConstructorDeclarationSyntax ctor when HasDefault(ctor.ParameterList):
                    return true;
                case LocalFunctionStatementSyntax local when local.ParameterList != null && HasDefault(local.ParameterList):
                    return true;
                case DelegateDeclarationSyntax @delegate when @delegate.ParameterList != null && HasDefault(@delegate.ParameterList):
                    return true;
                case TypeDeclarationSyntax type when type.ParameterList != null && HasDefault(type.ParameterList):
                    return true;
            }
        }
        return false;
    }

    private static bool HasDefault(ParameterListSyntax parameterList)
        => parameterList.Parameters.Any(p => p.Default != null);
}
