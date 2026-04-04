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
    /// Emits a TODO comment for unregistered expression kinds instead of throwing.
    /// </summary>
    /// <param name="node">The C# expression syntax node.</param>
    /// <param name="context">The conversion context.</param>
    /// <returns>Java code string.</returns>
    public string Transform(ExpressionSyntax node, ConversionContext context)
    {
        var transformer = ExpressionTransformerRegistry.GetTransformer(node.Kind());
        if (transformer == null)
        {
            context.Diagnostics.Warning(
                $"Expression kind {node.Kind()} is not yet supported. Emitting TODO comment.",
                node.GetLocation());
            return $"/* TODO: {node.Kind()} – {node.ToFullString().Trim()} */";
        }
        return transformer.Transform(node, context);
    }

    /// <summary>
    /// Transform a C# expression to a structured Java IR expression.
    /// <para>
    /// If the underlying transformer implements <see cref="IIRExpressionTransformer"/>,
    /// its <c>TransformToIR</c> method is called directly. Otherwise, the string result
    /// from <see cref="Transform"/> is wrapped in a <see cref="Java.JavaRawExpression"/>
    /// with <see cref="Java.JavaRawExpression.ResolvedType"/> set from the Roslyn semantic model.
    /// </para>
    /// </summary>
    public Java.JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        var transformer = ExpressionTransformerRegistry.GetTransformer(node.Kind());

        // If the transformer natively supports IR output, use it directly
        if (transformer is IIRExpressionTransformer irTransformer)
        {
            var irResult = irTransformer.TransformToIR(node, context);
            // Propagate resolved type if not already set
            if (irResult is Java.JavaRawExpression raw && raw.ResolvedType == null)
                raw.ResolvedType = ResolveJavaType(node, context);
            return irResult;
        }

        // Fallback: get string and wrap in JavaRawExpression with type info
        var code = Transform(node, context);
        var resolvedType = ResolveJavaType(node, context);
        return new Java.JavaRawExpression(code, resolvedType);
    }

    /// <summary>
    /// Resolves the Java type of a C# expression using the semantic model.
    /// Returns null if the semantic model is unavailable or the type cannot be resolved.
    /// </summary>
    private static string? ResolveJavaType(ExpressionSyntax node, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return null;

        var typeInfo = context.SemanticModel.GetTypeInfo(node);
        var type = typeInfo.Type ?? typeInfo.ConvertedType;
        if (type == null || type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Error)
            return null;

        var javaType = context.MapType(type);
        return string.IsNullOrWhiteSpace(javaType) ? null : javaType;
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
    public string TransformWhenNotNull(ExpressionSyntax expr, string objExpr, ConversionContext context)
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

            case ConditionalAccessExpressionSyntax nested:
                var nestedObj = TransformWhenNotNull(nested.Expression, objExpr, context);
                return $"({nestedObj} != null ? {TransformWhenNotNull(nested.WhenNotNull, nestedObj, context)} : null)";

            default:
                // Unknown structure; fall back to Transform(), which now emits a TODO comment
                // for unregistered kinds rather than throwing.
                return Transform(expr, context);
        }
    }
}
