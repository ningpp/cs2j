using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Minimal expression-position statement lowering for C36 (design §3.3-4
/// tech-debt slot): C24's <c>InitializerContextLowering</c> is not merged yet,
/// so M1 carries a minimal own implementation; when C24 lands the shared parts
/// are extracted up to <c>Shared/</c> append-only.
///
/// M0 exposes only the liftability predicate (delegating to the shared facts)
/// and no rewriting.
/// </summary>
internal static class CollectionContextLowering
{
    /// <summary>
    /// True when a spread-bearing site at <paramref name="node"/> can be turned
    /// into a temp plus statement sequence (local declaration RHS / return /
    /// yield / throw / assignment / expression statement / ternary branch /
    /// argument interleaving via an enclosing statement or arrow clause).
    /// </summary>
    public static bool CanLowerAt(SyntaxNode node)
        => ExpressionDesugarSyntaxFacts.IsInLiftableContext(node);

    /// <summary>
    /// M1: rewrites the enclosing context to consume the temp identifier.
    /// Not wired in M0.
    /// </summary>
    public static SyntaxNode? TryLower(
        SyntaxNode root,
        CollectionExpressionSyntax site,
        string tempIdentifier)
        => null;
}
