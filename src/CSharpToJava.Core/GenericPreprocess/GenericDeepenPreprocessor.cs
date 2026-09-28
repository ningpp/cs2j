using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>
/// Core entry for the generic-deepen pass: bind the whole-tree compilation once,
/// analyze symbol-level plans, then rewrite file trees and expose generated files.
/// </summary>
public sealed class GenericDeepenPreprocessor
{
    private Compilation? _compilation;
    private GenericDeepenAnalysis _analysis = GenericDeepenAnalysis.Empty;
    private GenericDeepenOptions _options = new();

    public GenericDeepenAnalysis Analysis => _analysis;

    public void BindCompilation(Compilation compilation) => _compilation = compilation;

    public GenericDeepenAnalysis Analyze(GenericDeepenOptions options)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        _options = options;
        _analysis = new GenericDeepenAnalyzer(_compilation, options).Analyze();
        return _analysis;
    }

    /// <summary>Rewrite the syntax tree backing <paramref name="model"/> (the same tree instance the plans were built from).</summary>
    public GenericDeepenFileResult RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        var stats = new GenericDeepenStatistics
        {
            FilesScanned = 1,
        };
        var diagnostics = new List<GenericDeepenDiagnostic>();
        var byNode = new Dictionary<SyntaxNode, TokenAnchorPlan>();
        foreach (var plan in _analysis.Anchors)
        {
            if (plan.Declaration.SyntaxTree == model.SyntaxTree)
            {
                byNode[plan.Declaration] = plan;
            }
        }

        if (byNode.Count == 0)
        {
            stats.FilesSkippedClean = 1;
            return new GenericDeepenFileResult(model.SyntaxTree.ToString(), false, diagnostics, stats);
        }

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var rewriter = new GenericDeepenRewriter(byNode, stats, diagnostics, model.SyntaxTree.FilePath);
        var newRoot = rewriter.Visit(root);
        if (newRoot == root)
        {
            stats.FilesSkippedClean = 1;
            return new GenericDeepenFileResult(model.SyntaxTree.ToString(), false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new GenericDeepenFileResult(TryFormat(newRoot), true, diagnostics, stats);
    }

    private static string TryFormat(SyntaxNode root)
    {
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
