using CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.DefaultParameterStripper;

public sealed class DefaultParameterStripper
{
    private readonly DefaultParameterCatalog _catalog = new();
    private readonly ParamsSignatureCollector _paramsCollector = new();

    /// <summary>
    /// C48 params-collections Collect result. Populated by <see cref="Collect"/>
    /// when the rule set is enabled in the options; null (zero behavior)
    /// otherwise. The declaration materializer and call-site rewriter (the
    /// R-a / R-b hooks of the following milestone) read this entry point.
    /// </summary>
    public ParamsCollectionsResult? ParamsResult { get; private set; }

    /// <summary>Pass 1: scan a syntax tree and register existing signatures.</summary>
    public void Collect(SyntaxTree tree, SemanticModel model, DefaultParameterStripperOptions? options = null)
    {
        _catalog.Collect(tree, model);
        if (options?.ParamsCollections is { Enabled: true } paramsOptions)
        {
            ParamsResult ??= new ParamsCollectionsResult();
            _paramsCollector.Collect(tree, model, paramsOptions, ParamsResult);
        }
    }

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
        if (!hasDefaults && options.ParamsCollections is { Enabled: true } paramsOptions)
        {
            // C48 additive dirty scan — only ever turns a clean file dirty,
            // never weakens the default-value verdict above. Short-circuited
            // when the rule is disabled (options.ParamsCollections null/off).
            hasDefaults = HasParamsCollectionsCandidate(root, paramsOptions.ExpandArrayCallSites);
        }

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

    /// <summary>
    /// C48 params-collections dirty scan (additive; consulted only when the
    /// rule is enabled): any parameter list whose last parameter carries the
    /// <c>params</c> keyword with a non-array type syntax is a params-collection
    /// candidate. Classic <c>params T[]</c> stays clean unless array expansion
    /// is enabled (strategy A keeps those declarations untouched; with
    /// <c>ExpandArrayCallSites</c> on, their call sites need rewriting too).
    /// </summary>
    private static bool HasParamsCollectionsCandidate(CompilationUnitSyntax root, bool expandArrayCallSites)
    {
        foreach (var parameterList in root.DescendantNodes().OfType<ParameterListSyntax>())
        {
            var parameters = parameterList.Parameters;
            if (parameters.Count == 0)
            {
                continue;
            }

            var last = parameters[^1];
            if (!last.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
            {
                continue;
            }

            if (expandArrayCallSites || last.Type is not ArrayTypeSyntax)
            {
                return true;
            }
        }

        return false;
    }
}
