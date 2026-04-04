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
            {
                var memberName = binding.Name.Identifier.Text;
                // Check property-to-method mapping (e.g., Count → size()) via semantic model
                if (context.SemanticModel?.GetSymbolInfo(binding).Symbol is IPropertySymbol prop)
                {
                    var mapped = TryMapPropertyToMethod(prop, context);
                    if (mapped != null)
                    {
                        if (mapped.Contains('.')) return mapped;
                        if (prop.ContainingType?.SpecialType == SpecialType.System_Array)
                            return $"{objExpr}.{mapped}";
                        return $"{objExpr}.{mapped}()";
                    }
                }
                // Fallback: when the semantic model can't resolve the member binding symbol
                // (e.g., in LINQ-rewritten code where synthesized syntax nodes lack symbol info),
                // resolve the type from the parent ConditionalAccessExpression and check TypeMappings.
                if (context.SemanticModel != null && binding.Parent is ConditionalAccessExpressionSyntax parentCond)
                {
                    var exprTypeInfo = context.SemanticModel.GetTypeInfo(parentCond.Expression);
                    var exprType = exprTypeInfo.Type ?? exprTypeInfo.ConvertedType;
                    if (exprType is INamedTypeSymbol namedExprType)
                    {
                        var mm = context.TypeMappings.MapMethod(namedExprType.ToDisplayString(), memberName);
                        if (mm == null)
                            mm = context.TypeMappings.MapMethod(
                                $"{namedExprType.ContainingNamespace}.{namedExprType.Name}", memberName);
                        if (mm == null)
                        {
                            foreach (var iface in namedExprType.AllInterfaces)
                            {
                                mm = context.TypeMappings.MapMethod(iface.ToDisplayString(), memberName);
                                if (mm == null)
                                    mm = context.TypeMappings.MapMethod(
                                        $"{iface.ContainingNamespace}.{iface.Name}", memberName);
                                if (mm != null) break;
                            }
                        }
                        if (mm != null)
                        {
                            if (mm.Contains('.')) return mm;
                            if (exprType.SpecialType == SpecialType.System_Array)
                                return $"{objExpr}.{mm}";
                            return $"{objExpr}.{mm}()";
                        }
                    }
                }
                return $"{objExpr}.{ConversionContext.EscapeJavaKeyword(memberName)}";
            }

            case InvocationExpressionSyntax invocation:
                // LINQ methods inside conditional access: ?.Select(...), ?.Where(...)
                // These won't be processed by InvocationExpressionTransformer's LINQ fallback,
                // so handle them here by building a stream pipeline.
                if (invocation.Expression is MemberBindingExpressionSyntax linqBinding
                    && linqBinding.Name.Identifier.Text is "Select" or "Where"
                    && invocation.ArgumentList.Arguments.Count >= 1)
                {
                    var linqMethodName = linqBinding.Name.Identifier.Text;
                    var streamOp = linqMethodName == "Where" ? "filter" : "map";
                    var linqArg = Transform(invocation.ArgumentList.Arguments[0].Expression, context);

                    // Determine receiver type for BuildStreamExpression
                    ITypeSymbol? receiverType = null;
                    if (context.SemanticModel != null
                        && invocation.Parent is ConditionalAccessExpressionSyntax condParent)
                    {
                        receiverType = context.SemanticModel.GetTypeInfo(condParent.Expression).Type;
                    }
                    var streamExpr = Transformers.Expression.Utilities.ExpressionTransformerHelpers
                        .BuildStreamExpression(objExpr, receiverType, context);

                    // Add .collect() terminal when this is the outermost expression
                    bool needsCollect = invocation.Parent is not MemberAccessExpressionSyntax;
                    var terminal = "";
                    if (needsCollect)
                    {
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList");
                        terminal = ".collect(Collectors.toCollection(() -> new ArrayList<>()))";
                    }

                    return $"{streamExpr}.{streamOp}({linqArg}){terminal}";
                }

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

    /// <summary>
    /// Attempts to map a C# property symbol to a Java method name via TypeMappings.
    /// Checks the property's containing type, then its FQN, then walks all implemented interfaces.
    /// Returns null when no mapping is found.
    /// </summary>
    private static string? TryMapPropertyToMethod(IPropertySymbol prop, ConversionContext context)
    {
        var typeName = prop.ContainingType.ToDisplayString();
        var mapped = context.TypeMappings.MapMethod(typeName, prop.Name);
        if (mapped == null)
        {
            var fqn = $"{prop.ContainingType.ContainingNamespace}.{prop.ContainingType.Name}";
            mapped = context.TypeMappings.MapMethod(fqn, prop.Name);
        }
        if (mapped == null && prop.ContainingType.AllInterfaces.Length > 0)
        {
            foreach (var iface in prop.ContainingType.AllInterfaces)
            {
                mapped = context.TypeMappings.MapMethod(iface.ToDisplayString(), prop.Name);
                if (mapped == null)
                    mapped = context.TypeMappings.MapMethod(
                        $"{iface.ContainingNamespace}.{iface.Name}", prop.Name);
                if (mapped != null) break;
            }
        }
        return mapped;
    }
}
