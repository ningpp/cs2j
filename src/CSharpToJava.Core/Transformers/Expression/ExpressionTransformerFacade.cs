using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Facade for expression transformation. Uses ExpressionTransformerRegistry.
/// </summary>
public class ExpressionTransformerFacade : IExpressionTransformer
{
    private static readonly Lazy<ExpressionTransformerFacade> _instance = new(() => new());

    /// <summary>
    /// Get the singleton instance of the facade.
    /// </summary>
    public static ExpressionTransformerFacade Instance => _instance.Value;

    /// <summary>
    /// Transform a C# expression to Java code.
    /// </summary>
    /// <param name="node">The C# expression syntax node.</param>
    /// <param name="context">The conversion context.</param>
    /// <returns>Java code string.</returns>
    /// <exception cref="NotSupportedException">Thrown when the expression kind is not supported.</exception>
    public string Transform(ExpressionSyntax node, ConversionContext context)
    {
        var transformer = ExpressionTransformerRegistry.GetTransformer(node.Kind())
            ?? throw new NotSupportedException($"Expression kind {node.Kind()} is not supported.");

        return transformer.Transform(node, context);
    }

    /// <summary>
    /// Recursively transforms the WhenNotNull part of a conditional access expression,
    /// replacing leading MemberBindingExpressionSyntax nodes with objExpr references.
    /// Handles chains like ?.a.b.Method(args).
    /// </summary>
    /// <param name="expr">The expression to transform.</param>
    /// <param name="objExpr">The object expression to substitute.</param>
    /// <param name="context">The conversion context.</param>
    /// <returns>Java code string.</returns>
    internal string TransformWhenNotNull(ExpressionSyntax expr, string objExpr, ConversionContext context)
    {
        switch (expr)
        {
            case MemberBindingExpressionSyntax binding:
                return $"{objExpr}.{ConversionContext.EscapeJavaKeyword(binding.Name.Identifier.Text)}";

            case InvocationExpressionSyntax invocation:
                var invokedTarget = TransformWhenNotNull(invocation.Expression, objExpr, context);
                var invArgs = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
                return $"{invokedTarget}({invArgs})";

            case MemberAccessExpressionSyntax memberAccess:
                var accessTarget = TransformWhenNotNull(memberAccess.Expression, objExpr, context);
                return $"{accessTarget}.{ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text)}";

            case ElementAccessExpressionSyntax elementAccess:
                var elTarget = TransformWhenNotNull(elementAccess.Expression, objExpr, context);
                var elIdx = string.Join(", ", elementAccess.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
                return $"{elTarget}.get({elIdx})";

            default:
                // Unknown structure; fall back to transformed C# text (best effort)
                return Transform(expr, context);
        }
    }
}
