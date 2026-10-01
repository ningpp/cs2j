using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Syntax facts for C36: element-kind exhaustiveness and residual recognition
/// (design hard invariant #3, §4).
/// </summary>
internal static class CollectionExprSyntaxFacts
{
    /// <summary>
    /// Enumerates <b>every</b> element of a collection expression. The rule
    /// must account for each syntax kind — OfType&lt;ExpressionElementSyntax&gt;
    /// style silent filtering (the S2 data-loss bug) is forbidden. New element
    /// kinds added by later language versions get an explicit diagnostic, never
    /// a silent drop.
    /// </summary>
    public static IEnumerable<CollectionElementSyntax> EnumerateElements(
        CollectionExpressionSyntax node)
    {
        foreach (var element in node.Elements)
            yield return element;
    }

    /// <summary>
    /// Exhaustive classification. Anything beyond the two C# 12 element kinds
    /// becomes <see cref="CollectionElementKind.Unsupported"/> so the caller
    /// emits an explicit error instead of losing data.
    /// </summary>
    public static CollectionElementKind ClassifyElement(CollectionElementSyntax element)
        => element switch
        {
            ExpressionElementSyntax => CollectionElementKind.Expression,
            SpreadElementSyntax => CollectionElementKind.Spread,
            _ => CollectionElementKind.Unsupported,
        };

    public static bool IsSpread(CollectionElementSyntax element)
        => element is SpreadElementSyntax;

    /// <summary>True for the two node kinds the residual gate tracks.</summary>
    public static bool IsResidualNode(SyntaxNode node)
        => node is CollectionExpressionSyntax or SpreadElementSyntax;

    /// <summary>
    /// True when <paramref name="node"/> itself or any ancestor carries the
    /// family explained annotation: a preserved site accounts for the whole
    /// subtree, including its spread elements.
    /// </summary>
    public static bool IsExplained(SyntaxNode node)
    {
        var current = node;
        while (current != null)
        {
            if (current.HasAnnotation(ExpressionDesugarSyntaxFacts.ExplainedAnnotation))
                return true;
            current = current.Parent;
        }

        return false;
    }
}
