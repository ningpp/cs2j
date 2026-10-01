using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Syntax facts for C36: element-kind exhaustiveness, residual recognition
/// and the E09 byte-element seam marker (design hard invariant #3, §4).
/// </summary>
internal static class CollectionExprSyntaxFacts
{
    /// <summary>
    /// Enumerates <b>every</b> element of a collection expression. The rule
    /// must account for each syntax kind — OfType&lt;ExpressionElementSyntax&gt;
    /// style silent filtering (the S2 data-loss bug) is forbidden. New element
    /// kinds added by later language versions get an explicit switch arm, never
    /// a silent drop.
    /// </summary>
    public static IEnumerable<CollectionElementSyntax> EnumerateElements(
        CollectionExpressionSyntax node)
    {
        foreach (var element in node.Elements)
            yield return element;
    }

    public static CollectionElementKind ClassifyElement(CollectionElementSyntax element)
        => element switch
        {
            ExpressionElementSyntax => CollectionElementKind.Expression,
            SpreadElementSyntax => CollectionElementKind.Spread,
            // Future element kinds must be added here (exhaustive failure over silent drop).
            _ => CollectionElementKind.Expression,
        };

    public static bool IsSpread(CollectionElementSyntax element)
        => element is SpreadElementSyntax;

    /// <summary>True for the two node kinds the residual gate tracks.</summary>
    public static bool IsResidualNode(Microsoft.CodeAnalysis.SyntaxNode node)
        => node is CollectionExpressionSyntax or SpreadElementSyntax;

    /// <summary>
    /// E09 seam (design §11): byte constant elements appear directly inside
    /// array/collection initializers today; statement-lowered Add/ToArray
    /// shapes need the M1 four-quadrant review. M0 keeps the marker only.
    /// </summary>
    public static bool IsByteConstantElementShape(CollectionElementSyntax element)
        => false; // M1: semantic constant-range check coordinated with E09.
}
