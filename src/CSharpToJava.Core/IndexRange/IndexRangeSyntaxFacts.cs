using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.IndexRange;

/// <summary>Syntax predicates, inline whitelist and context feasibility checks.</summary>
internal static class IndexRangeSyntaxFacts
{
    internal const int IndexExpressionKind = (int)SyntaxKind.IndexExpression;
    internal const int RangeExpressionKind = (int)SyntaxKind.RangeExpression;

    /// <summary>Annotation for nodes deliberately left in place under a diagnostic.</summary>
    internal static readonly SyntaxAnnotation CoveredAnnotation =
        new("cs2j-index-range", "covered");

    /// <summary>Annotation for baseline-illegal sites (multidim/pointer), left verbatim.</summary>
    internal static readonly SyntaxAnnotation SkippedAnnotation =
        new("cs2j-index-range", "skipped");

    /// <summary>Dirty check: tree contains an IndexExpression or RangeExpression node.</summary>
    internal static bool IsDirty(SyntaxNode root)
        => root.DescendantNodesAndSelf().Any(IsIndexOrRangeNode);

    internal static bool IsIndexOrRangeNode(SyntaxNode node)
        => node.RawKind == IndexExpressionKind || node.RawKind == RangeExpressionKind;

    internal static bool IsIndexNode(SyntaxNode node)
        => node.RawKind == IndexExpressionKind;

    internal static bool IsRangeNode(SyntaxNode node)
        => node.RawKind == RangeExpressionKind;

    /// <summary>Whether the expression is an int literal with the given value.</summary>
    internal static bool IsIntLiteral(ExpressionSyntax? expression, int value)
        => expression != null
           && TryGetIntLiteral(expression, out var literal)
           && literal == value;

    internal static bool TryGetIntLiteral(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.NumericLiteralExpression))
            return false;
        if (literal.Token.Value is int intValue)
        {
            value = intValue;
            return true;
        }

        return false;
    }

    internal static LiteralExpressionSyntax IntLiteral(int value)
        => Microsoft.CodeAnalysis.CSharp.SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(value));

    /// <summary>
    /// Builds <c>left - right</c>, folding trivially simplifiable shapes:
    /// <c>x - 0</c>, constant-constant and <c>(x - c1) - c2</c>.
    /// Returns null when no folding applies (caller emits the binary node).
    /// </summary>
    internal static ExpressionSyntax? TryFoldSubtract(
        ExpressionSyntax left, ExpressionSyntax right)
    {
        if (IsIntLiteral(right, 0))
            return left;

        if (TryGetIntLiteral(left, out var leftValue)
            && TryGetIntLiteral(right, out var rightValue))
        {
            return IntLiteral(leftValue - rightValue);
        }

        // (x - c1) - c2  ==  x - (c1 + c2)
        if (left is BinaryExpressionSyntax inner
            && inner.IsKind(SyntaxKind.SubtractExpression)
            && TryGetIntLiteral(inner.Right, out var c1)
            && TryGetIntLiteral(right, out var c2))
        {
            var foldedRight = IntLiteral(c1 + c2);
            return IsIntLiteral(foldedRight, 0)
                ? inner.Left
                : TryFoldSubtract(inner.Left, foldedRight)
                  ?? Microsoft.CodeAnalysis.CSharp.SyntaxFactory.BinaryExpression(
                      SyntaxKind.SubtractExpression, inner.Left, foldedRight);
        }

        return null;
    }

    /// <summary>
    /// Structural purity: the expression has no possible side effect, so it
    /// may be textually duplicated in the output. Conservative default false.
    /// </summary>
    internal static bool IsPure(ExpressionSyntax? expression)
    {
        switch (expression)
        {
            case null:
                return true;
            case LiteralExpressionSyntax:
            case IdentifierNameSyntax:
            case DefaultExpressionSyntax:
            case TypeOfExpressionSyntax:
            case SizeOfExpressionSyntax:
                return true;
            case ParenthesizedExpressionSyntax pe:
                return IsPure(pe.Expression);
            case CheckedExpressionSyntax ce:
                return IsPure(ce.Expression);
            case MemberAccessExpressionSyntax ma:
                return IsPure(ma.Expression);
            case ConditionalExpressionSyntax c:
                return IsPure(c.Condition) && IsPure(c.WhenTrue) && IsPure(c.WhenFalse);
            case BinaryExpressionSyntax b:
                return IsPure(b.Left) && IsPure(b.Right);
            case PrefixUnaryExpressionSyntax pu:
                return pu.IsKind(SyntaxKind.UnaryMinusExpression)
                       || pu.IsKind(SyntaxKind.UnaryPlusExpression)
                       || pu.IsKind(SyntaxKind.BitwiseNotExpression)
                       || pu.RawKind == IndexExpressionKind
                    ? IsPure(pu.Operand)
                    : false;
            case CastExpressionSyntax ce:
                return IsPure(ce.Expression);
            case ElementAccessExpressionSyntax ea:
                return IsPure(ea.Expression)
                       && ea.ArgumentList.Arguments.All(
                           a => a.Expression is ExpressionSyntax ex && IsPure(ex));
            case ArrayCreationExpressionSyntax ac:
                return ac.Initializer == null
                       || ac.Initializer.Expressions.All(
                           e => e is ExpressionSyntax ex && IsPure(ex));
            case ImplicitArrayCreationExpressionSyntax ia:
                return ia.Initializer.Expressions.All(
                    e => IsPure(e as ExpressionSyntax));
            case InitializerExpressionSyntax init:
                return init.Expressions.All(
                    e => e is ExpressionSyntax ex && IsPure(ex));
            case IsPatternExpressionSyntax ip:
                return IsPure(ip.Expression);
            case InterpolatedStringExpressionSyntax interp:
                return interp.Contents.All(c =>
                    c is InterpolatedStringTextSyntax
                    || c is InterpolationSyntax i && IsPure(i.Expression));
            default:
                return false;
        }
    }

    /// <summary>
    /// True when the node lives in a context where no temp declaration can be
    /// anchored: attribute argument, field/property/event initializer, query
    /// clause or an expression-tree lambda.
    /// </summary>
    internal static bool IsNoTempContext(
        SyntaxNode node,
        SemanticModel model,
        out string reason)
    {
        if (node.Ancestors().OfType<AttributeSyntax>().Any())
        {
            reason = "attribute argument";
            return true;
        }

        if (node.Ancestors().OfType<QueryExpressionSyntax>().Any())
        {
            reason = "LINQ query expression";
            return true;
        }

        if (node.Ancestors().Any(a =>
                a is FieldDeclarationSyntax or EventFieldDeclarationSyntax
                    or PropertyDeclarationSyntax or IndexerDeclarationSyntax))
        {
            reason = "field/property initializer";
            return true;
        }

        foreach (var lambda in node.Ancestors().OfType<LambdaExpressionSyntax>())
        {
            var converted = model.GetTypeInfo(lambda).ConvertedType;
            if (converted is INamedTypeSymbol { IsGenericType: true } named
                && named.ConstructedFrom.ToDisplayString()
                    == "System.Linq.Expressions.Expression<TDelegate>")
            {
                reason = "expression-tree lambda";
                return true;
            }
        }

        reason = "";
        return false;
    }

    /// <summary>
    /// True when the element access is the WhenNotNull face of a
    /// conditional-access (<c>arr?[...]</c>, C35 territory).
    /// </summary>
    internal static bool IsConditionalAccessFace(ElementAccessExpressionSyntax node)
    {
        if (node.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any())
            return true;

        return false;
    }
}
