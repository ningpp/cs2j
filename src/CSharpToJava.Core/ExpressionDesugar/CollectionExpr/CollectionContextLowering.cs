using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Minimal expression-position statement lowering for C36.
///
/// C24-TECH-DEBT: C24's <c>InitializerContextLowering</c> is not merged yet;
/// M1 carries this minimal own implementation. When C24 lands the shared parts
/// are extracted up to <c>Shared/</c> append-only (plan §0.3, design §3.3-4).
///
/// Safe positions (design §2.4): local-declaration RHS, return/yield return,
/// throw, assignment RHS, expression-bodied members (the family TempLifter
/// expands arrow clauses to blocks). Ternary branches / argument positions are
/// allowed only when no observable side effect precedes the site inside the
/// enclosing statement; otherwise CS2J0583. Anchor-free positions (field /
/// property initializers, attribute arguments, default parameter values, query
/// clauses) are always CS2J0583 for spread-bearing sites; zero-temp forms are
/// embeddable anywhere and never consult this gate.
/// </summary>
internal static class CollectionContextLowering
{
    /// <summary>
    /// Decides whether a spread-bearing site can be lifted at its position.
    /// Returns true when safe; otherwise false and <paramref name="reason"/>
    /// carries the (invariant) phrase embedded in the CS2J0583 message.
    /// </summary>
    public static bool CanLowerAt(CollectionExpressionSyntax site, out string reason)
    {
        // Anchor-free contexts first (field/property initializer, attribute,
        // default parameter value, query clause).
        if (!ExpressionDesugarSyntaxFacts.IsInLiftableContext(site))
        {
            reason = "field/property initializer, attribute argument, default " +
                     "parameter value or query clause";
            return false;
        }

        var anchor = ExpressionDesugarTempLifter.FindAnchor(site);

        // Arrow clause (expression-bodied method/local function/property/...):
        // TempLifter expands it to a block inside the member — statements stay
        // at the exact evaluation position. A lambda's own arrow is NOT
        // expandable by the shared lifter (there is no member block to
        // rewrite), so M1 treats it as non-liftable; block-bodied lambdas are
        // handled by the ordinary statement anchor below.
        if (anchor is ArrowExpressionClauseSyntax arrowAnchor)
        {
            if (arrowAnchor.Parent is AnonymousFunctionExpressionSyntax)
            {
                reason = "an expression-bodied lambda (M1 only lifts inside " +
                         "block-bodied lambdas)";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        if (anchor is not StatementSyntax anchorStatement)
        {
            reason = "a position with no statement anchor";
            return false;
        }

        // A lambda/local-function boundary between the site and its statement
        // anchor: hoisting to the enclosing statement would build the temp
        // eagerly (captured closure semantics change). Block-bodied functions
        // have their own inner statement anchor and never reach this arm.
        if (IsSeparatedByFunctionBoundary(site, anchorStatement))
        {
            reason = "a lambda/local-function boundary (hoisting would change " +
                     "evaluation timing)";
            return false;
        }

        // Conservative single-evaluation guard (design §2.3 R2): only
        // literals/identifiers/call-free member access may precede the site in
        // the same statement.
        if (HasObservableSideEffectBefore(site, anchorStatement))
        {
            reason = "an observable side effect (call/object creation/" +
                     "assignment/++/--/throw/await) precedes the site in the " +
                     "same statement";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// True when an anonymous function or local-function declaration separates
    /// <paramref name="site"/> from <paramref name="anchorStatement"/> (the
    /// anchor is outside the function even though the site is inside it).
    /// </summary>
    private static bool IsSeparatedByFunctionBoundary(
        SyntaxNode site, StatementSyntax anchorStatement)
    {
        var current = site.Parent;
        while (current != null && current != anchorStatement)
        {
            if (current is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    /// <summary>
    /// Conservative scan: any side-effect-capable node strictly before
    /// <paramref name="site"/> inside <paramref name="anchorStatement"/> that
    /// is not one of the site's own ancestors rejects lifting. Ancestors are
    /// excluded because they evaluate together with the site (e.g. the
    /// <c>new X([..e])</c> creation or the enclosing call receiving the site).
    /// </summary>
    private static bool HasObservableSideEffectBefore(
        CollectionExpressionSyntax site, StatementSyntax anchorStatement)
    {
        var siteStart = site.SpanStart;

        foreach (var node in anchorStatement.DescendantNodes())
        {
            if (node.SpanStart >= siteStart)
                continue;

            if (IsAncestorOrSelf(node, site))
                continue;

            if (IsPotentiallyObservable(node))
                return true;
        }

        return false;
    }

    private static bool IsAncestorOrSelf(SyntaxNode candidate, SyntaxNode site)
    {
        var current = site;
        while (current != null)
        {
            if (current == candidate)
                return true;
            current = current.Parent;
        }

        return false;
    }

    private static bool IsPotentiallyObservable(SyntaxNode node)
    {
        switch (node)
        {
            case InvocationExpressionSyntax:
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
            case StackAllocArrayCreationExpressionSyntax:
            case ImplicitStackAllocArrayCreationExpressionSyntax:
            case AssignmentExpressionSyntax:
            case ThrowExpressionSyntax:
            case AwaitExpressionSyntax:
                return true;

            case PrefixUnaryExpressionSyntax prefix:
                return prefix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken)
                       || prefix.OperatorToken.IsKind(SyntaxKind.MinusMinusToken);

            case PostfixUnaryExpressionSyntax postfix:
                return postfix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken)
                       || postfix.OperatorToken.IsKind(SyntaxKind.MinusMinusToken);

            default:
                return false;
        }
    }
}
