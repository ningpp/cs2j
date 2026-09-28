using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Applies generic-deepen plans to one syntax tree of the bound compilation.</summary>
internal sealed class GenericDeepenRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<SyntaxNode, TokenAnchorPlan> _anchorsByNode;
    private readonly GenericDeepenStatistics _stats;
    private readonly List<GenericDeepenDiagnostic> _diagnostics;
    private readonly string _filePath;

    public GenericDeepenRewriter(
        Dictionary<SyntaxNode, TokenAnchorPlan> anchorsByNode,
        GenericDeepenStatistics stats,
        List<GenericDeepenDiagnostic> diagnostics,
        string filePath)
    {
        _anchorsByNode = anchorsByNode;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }
}
