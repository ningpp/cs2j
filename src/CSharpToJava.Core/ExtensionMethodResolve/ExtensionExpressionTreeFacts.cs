using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Facts for "is this site located inside an <c>Expression&lt;...&gt;</c>
/// lambda?" (EM-EXPRTREE, design §4.1). Delegates the actual lambda verdict to
/// the shared <see cref="ExpressionTreeFacts"/> (C02) — no second copy.
/// </summary>
internal static class ExtensionExpressionTreeFacts
{
    /// <summary>
    /// True when <paramref name="node"/> is inside (or is) an expression-tree
    /// lambda: the nearest enclosing lambda converts to
    /// <c>System.Linq.Expressions.Expression&lt;TDelegate&gt;</c>.
    /// </summary>
    public static bool IsInExpressionTreeLambda(SyntaxNode? node, SemanticModel model)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is LambdaExpressionSyntax lambda
                && ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The nearest enclosing expression-tree lambda, or null.</summary>
    public static LambdaExpressionSyntax? FindEnclosingExpressionTreeLambda(
        SyntaxNode? node,
        SemanticModel model)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is LambdaExpressionSyntax lambda
                && ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
            {
                return lambda;
            }
        }

        return null;
    }
}
