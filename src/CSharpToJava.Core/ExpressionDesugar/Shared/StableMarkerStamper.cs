using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Applies every rule's stable-key annotation contribution in a single
/// bottom-up walk. Node instances refreshed by this (and by later rule
/// rewrites) are irrelevant: the attached <see cref="SyntaxAnnotation"/>s
/// survive subtree replacements and are how cross-rule plans resolve their
/// original analysis sites on the progressive tree.
/// </summary>
internal sealed class StableMarkerStamper : CSharpSyntaxRewriter
{
    private readonly Dictionary<SyntaxNode, List<SyntaxAnnotation>> _stamps;

    private StableMarkerStamper(
        Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps)
    {
        _stamps = stamps;
    }

    public static CompilationUnitSyntax Apply(
        CompilationUnitSyntax root,
        Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps)
    {
        var stamper = new StableMarkerStamper(stamps);
        return (CompilationUnitSyntax)stamper.Visit(root)!;
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var visited = base.Visit(node);
        if (node != null
            && visited != null
            && _stamps.TryGetValue(node, out var annotations)
            && annotations.Count > 0)
        {
            visited = visited.WithAdditionalAnnotations(annotations.ToArray());
        }

        return visited;
    }
}
