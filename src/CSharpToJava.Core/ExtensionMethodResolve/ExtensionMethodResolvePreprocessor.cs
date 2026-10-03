using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Core entry for the extension-method-resolve pass (C19): bind the
/// whole-tree compilation once, analyze reduced extension call sites per
/// syntax tree, then rewrite each file and expose the residual scan. No
/// cross-file generated products exist for this pass.
///
/// M1: EM-BIND/EM-CALL/EM-GEN/EM-GEN-HOST/EM-CHAIN/EM-IDEAL/EM-LINQ/EM-EXT14.
/// Files with no selected site return Changed=false; the runner performs a
/// byte-level copy then (BOM preserved). Changed files use three-level
/// formatting: changed regions (outermost rewritten invocations) → whole file
/// normalization → original tree text.
/// </summary>
public sealed class ExtensionMethodResolvePreprocessor
{
    private sealed class FilePlan
    {
        public required IReadOnlyList<ExtensionCallBinding> Bindings { get; init; }
        public required IReadOnlyList<ExtensionDiagnostic> Diagnostics { get; init; }
        public required ExtensionMethodResolveStatistics Statistics { get; init; }
    }

    private Compilation? _compilation;
    private ExtensionMethodResolveOptions _options = new();
    private readonly Dictionary<SyntaxTree, FilePlan> _plans = new();
    private IReadOnlyList<ExtensionRewrittenSiteKey> _rewrittenSites = Array.Empty<ExtensionRewrittenSiteKey>();

    public void BindCompilation(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
    }

    /// <summary>Whole-tree analysis: per-file binding tables, sorted deterministically by file path + span.</summary>
    public void Analyze(ExtensionMethodResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        _options = options;
        _plans.Clear();

        var sites = new List<ExtensionRewrittenSiteKey>();

        foreach (var tree in _compilation.SyntaxTrees.OrderBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var model = _compilation.GetSemanticModel(tree);
            var binder = new ExtensionCallBinder(model, _options);
            binder.Analyze();

            _plans[tree] = new FilePlan
            {
                Bindings = binder.Bindings,
                Diagnostics = binder.Diagnostics,
                Statistics = binder.Statistics,
            };

            foreach (var binding in binder.Bindings)
            {
                var unreducedDefinition = binding.Unreduced.OriginalDefinition;
                sites.Add(new ExtensionRewrittenSiteKey(
                    tree.FilePath,
                    binding.Invocation.SpanStart,
                    unreducedDefinition.ToDisplayString(),
                    1 + binding.Invocation.ArgumentList.Arguments.Count,
                    unreducedDefinition.Parameters
                        .Select(p => p.Type.ToDisplayString())
                        .ToList()));
            }
        }

        _rewrittenSites = sites;
    }

    /// <summary>Rewrite the syntax tree backing <paramref name="model"/> (the same tree analyzed in <see cref="Analyze"/>).</summary>
    public ExtensionFileResult RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        ArgumentNullException.ThrowIfNull(model);

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

        if (!_plans.TryGetValue(model.SyntaxTree, out var plan))
        {
            // Synthesized trees (e.g. injected global usings) are never rewritten.
            return new ExtensionFileResult(
                root.ToFullString(),
                Changed: false,
                Array.Empty<ExtensionDiagnostic>(),
                new ExtensionMethodResolveStatistics { FilesScanned = 1, FilesClean = 1 });
        }

        var stats = plan.Statistics;
        stats.FilesScanned++;

        if (plan.Bindings.Count == 0)
        {
            stats.FilesClean++;
            return new ExtensionFileResult(root.ToFullString(), Changed: false, plan.Diagnostics, stats);
        }

        var bindingsByNode = new Dictionary<SyntaxNode, ExtensionCallBinding>();
        foreach (var binding in plan.Bindings)
        {
            bindingsByNode[binding.Invocation] = binding;
        }

        var rewriter = new ExtensionCallRewriter(bindingsByNode);
        var newRoot = (CompilationUnitSyntax?)rewriter.Visit(root);

        if (newRoot is null
            || !newRoot.GetAnnotatedNodes(ExtensionCallRewriter.RewrittenAnnotation).Any())
        {
            stats.FilesClean++;
            return new ExtensionFileResult(root.ToFullString(), Changed: false, plan.Diagnostics, stats);
        }

        stats.FilesTransformed++;
        stats.CallsitesRewritten += plan.Bindings.Count;
        stats.GenericHostQualified += plan.Bindings.Count(b => b.Unreduced.ContainingType.IsGenericType);

        var output = Format(newRoot);
        return new ExtensionFileResult(output, Changed: true, plan.Diagnostics, stats);
    }

    /// <summary>Post-rewrite residual scan over the recompiled intermediate tree (gates 1/3/6 in M1).</summary>
    public Dictionary<string, int> ResidualScan(Compilation rewritten)
        => ExtensionMethodResidualScanner.Scan(rewritten, _rewrittenSites, _options);

    /// <summary>
    /// Three-level formatting: (1) NormalizeWhitespace on the outermost
    /// rewritten regions only, preserving everything else byte-for-byte;
    /// (2) whole-file NormalizeWhitespace if region formatting fails;
    /// (3) raw tree text as the last resort.
    /// </summary>
    private static string Format(CompilationUnitSyntax root)
    {
        try
        {
            var rewritten = root.GetAnnotatedNodes(ExtensionCallRewriter.RewrittenAnnotation)
                .OfType<InvocationExpressionSyntax>()
                .ToList();
            var outermost = rewritten
                .Where(node => !rewritten.Any(other => other != node && node.Ancestors().Contains(other)))
                .ToList();

            if (outermost.Count > 0)
            {
                var formatted = outermost.ToDictionary(
                    node => (SyntaxNode)node,
                    node =>
                    {
                        var leading = node.GetLeadingTrivia();
                        return (SyntaxNode)node.NormalizeWhitespace().WithLeadingTrivia(leading);
                    });
                var regionRoot = root.ReplaceNodes(outermost, (original, _) => formatted[original]);
                return regionRoot.ToFullString();
            }
        }
        catch
        {
            // Fall through to whole-file formatting.
        }

        try
        {
            return root.NormalizeWhitespace().ToFullString();
        }
        catch
        {
            return root.ToFullString();
        }
    }
}
