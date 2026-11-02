using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Analysis;

/// <summary>
/// Shared predicate for lambdas assigned to Expression&lt;TDelegate&gt; (expression trees).
/// Extracted from JavaKeywordSyntaxFacts so every pass uses one definition (catalog §4 risk 8).
/// </summary>
public static class ExpressionTreeFacts
{
    public static bool IsExpressionTreeLambda(SemanticModel model, LambdaExpressionSyntax lambda)
    {
        var typeInfo = model.GetTypeInfo(lambda);
        var convertedType = typeInfo.ConvertedType;
        if (convertedType == null) return false;
        var fullName = convertedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName.StartsWith("global::System.Linq.Expressions.Expression<", StringComparison.Ordinal);
    }

    /// <summary>
    /// M3 (C26 design §4.4): returns the nearest enclosing lambda that is
    /// quoted as <c>Expression&lt;TDelegate&gt;</c> when <paramref name="node"/>
    /// sits lexically inside a quoted expression-tree lambda, otherwise null.
    /// <para>
    /// A node inside a quoted lambda — including nodes inside a Func-typed
    /// nested lambda within the tree — belongs to the expression tree and no
    /// channel pass may rewrite it. C# has no unquote/splice syntax in plain
    /// source, so lexical containment inside ANY ancestor lambda whose
    /// converted type is <c>Expression&lt;&gt;</c> is sufficient.
    /// </para>
    /// <para>
    /// Identity is established first from the lambda node's own
    /// <c>ConvertedType</c> (the common path: <c>q.Where(x => …)</c>,
    /// <c>Expression&lt;Func&lt;…&gt;&gt; p = x => …</c>) and, when the
    /// semantic model gives the lambda no converted type, from the enclosing
    /// invocation's bound parameter at the lambda's argument position
    /// (the "ancestor call argument site" query).
    /// </para>
    /// </summary>
    public static LambdaExpressionSyntax? FindEnclosingQuotedLambda(
        SemanticModel model,
        SyntaxNode node)
    {
        foreach (var lambda in node.AncestorsAndSelf().OfType<LambdaExpressionSyntax>())
        {
            if (IsExpressionTreeLambda(model, lambda))
            {
                return lambda;
            }

            if (LambdaArgumentSiteConvertsToExpression(model, lambda))
            {
                return lambda;
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="node"/> is inside a quoted
    /// <c>Expression&lt;TDelegate&gt;</c> lambda (see
    /// <see cref="FindEnclosingQuotedLambda"/>).
    /// </summary>
    public static bool IsInsideQuotedExpressionLambda(SemanticModel model, SyntaxNode node)
        => FindEnclosingQuotedLambda(model, node) is not null;

    /// <summary>
    /// Precomputed per-file quoted-lambda set. Answers the same
    /// <see cref="IsInsideQuotedExpressionLambda"/> question as
    /// <see cref="FindEnclosingQuotedLambda"/> but evaluates each lambda's
    /// semantic predicates exactly once instead of once per queried
    /// descendant; membership tests afterwards are pure syntax walks.
    /// </summary>
    public sealed class QuotedLambdaScope
    {
        private readonly HashSet<LambdaExpressionSyntax> _quotedLambdas;

        private QuotedLambdaScope(HashSet<LambdaExpressionSyntax> quotedLambdas)
            => _quotedLambdas = quotedLambdas;

        public static QuotedLambdaScope Build(SemanticModel model, SyntaxNode root)
        {
            var quoted = new HashSet<LambdaExpressionSyntax>();
            foreach (var lambda in root.DescendantNodes().OfType<LambdaExpressionSyntax>())
            {
                // Same predicates (and evaluation order) as
                // FindEnclosingQuotedLambda per ancestor lambda, with one
                // provable short-circuit: when the lambda HAS a concrete
                // converted type that is not Expression<>, the argument-site
                // query cannot add anything — at an argument site the bound
                // parameter type IS the converted type, and away from one the
                // query is false anyway.
                var converted = model.GetTypeInfo(lambda).ConvertedType;
                if (converted is not null)
                {
                    var display = converted.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (display.StartsWith(
                            "global::System.Linq.Expressions.Expression<",
                            StringComparison.Ordinal))
                    {
                        quoted.Add(lambda);
                    }

                    continue;
                }

                if (LambdaArgumentSiteConvertsToExpression(model, lambda))
                {
                    quoted.Add(lambda);
                }
            }

            return new QuotedLambdaScope(quoted);
        }

        /// <summary>
        /// True when <paramref name="node"/> sits lexically inside any quoted
        /// lambda (equivalent to <see cref="IsInsideQuotedExpressionLambda"/>
        /// for non-lambda nodes, which is every LINQ call-site use).
        /// </summary>
        public bool IsInside(SyntaxNode node)
        {
            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor is LambdaExpressionSyntax lambda && _quotedLambdas.Contains(lambda))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Ancillary "ancestor call argument site" query (design §4.4): true when
    /// <paramref name="lambda"/> is passed as an argument whose bound
    /// parameter type is <c>Expression&lt;TDelegate&gt;</c>. Covers shapes
    /// where the lambda node itself carries no usable <c>ConvertedType</c>;
    /// never changes the result for ordinary <c>Func&lt;&gt;</c> arguments.
    /// </summary>
    public static bool LambdaArgumentSiteConvertsToExpression(
        SemanticModel model,
        LambdaExpressionSyntax lambda)
    {
        if (lambda.Parent is not ArgumentSyntax argument
            || argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        var index = argumentList.Arguments.IndexOf(argument);
        if (index < 0)
        {
            return false;
        }

        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || index >= method.Parameters.Length)
        {
            // LINQ Expression<>-bearing operators never use params arrays; an
            // out-of-range argument is an error shape, not a quote site.
            return false;
        }

        var parameterType = method.Parameters[index].Type;
        var display = parameterType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return display is not null
            && display.StartsWith(
                "global::System.Linq.Expressions.Expression<",
                StringComparison.Ordinal);
    }
}
