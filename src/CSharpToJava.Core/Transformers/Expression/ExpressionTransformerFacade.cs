using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Expression.Utilities;

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

        var typeInfo = context.GetTypeInfo(node);
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
                var prop = context.GetSymbolInfo(binding).Symbol as IPropertySymbol;

                // Check method binding via semantic model (e.g., ?.ToString(), ?.GetHashCode())
                if (context.GetSymbolInfo(binding).Symbol is IMethodSymbol method)
                {
                    var mapped = ApplyMethodBindingNameMapping(method, memberName, context);
                    return $"{objExpr}.{mapped}";
                }

                if (binding.Parent is InvocationExpressionSyntax)
                {
                    return $"{objExpr}.{ApplyMethodBindingNameMapping(null, memberName, context)}";
                }

                // Check property-to-method mapping (e.g., Count → size()) via semantic model
                if (prop != null)
                {
                    // Use the expression's static type for lookup (e.g., SortedList not IDictionary)
                    // so that concrete-type mappings like SortedList.Values→getValues take
                    // priority over interface mappings like IDictionary.Values→values.
                    ITypeSymbol? exprType = null;
                    ConditionalAccessExpressionSyntax? ownerCond2 = null;
                    var ancestor2 = binding.Parent;
                    while (ancestor2 != null)
                    {
                        if (ancestor2 is ConditionalAccessExpressionSyntax ca2)
                        { ownerCond2 = ca2; break; }
                        ancestor2 = ancestor2.Parent;
                    }
                    if (ownerCond2 != null)
                        exprType = context.GetTypeInfo(ownerCond2.Expression).Type;
                    var mapped = TryMapPropertyToMethod(prop, exprType, context);
                    if (mapped != null)
                    {
                        if (ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(mapped))
                            return $"{mapped}()";
                        if (mapped.Contains('.')) return mapped;
                        if (prop.ContainingType?.SpecialType == SpecialType.System_Array)
                            return $"{objExpr}.{mapped}";
                        return $"{objExpr}.{mapped}()";
                    }
                }
                // Fallback: when the semantic model can't resolve the member binding symbol
                // (e.g., in LINQ-rewritten code where synthesized syntax nodes lack symbol info),
                // resolve the type from the owning ConditionalAccessExpression and check TypeMappings.
                // Walk ancestors — the binding may be nested inside MemberAccessExpressionSyntax
                // (e.g. ?.Nodes.Remove where .Nodes is inside .Nodes.Remove member access).
                ConditionalAccessExpressionSyntax? ownerCond = null;
                var ancestor = binding.Parent;
                while (ancestor != null)
                {
                    if (ancestor is ConditionalAccessExpressionSyntax ca)
                    {
                        ownerCond = ca;
                        break;
                    }
                    ancestor = ancestor.Parent;
                }
                if (context.SemanticModel != null && ownerCond != null)
                {
                    var exprTypeInfo = context.GetTypeInfo(ownerCond.Expression);
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
                            Transformers.Expression.Utilities.ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mm, context);
                            if (Transformers.Expression.Utilities.ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(mm))
                                return $"{mm}()";
                            if (mm.Contains('.')) return mm;
                            if (exprType.SpecialType == SpecialType.System_Array)
                                return $"{objExpr}.{mm}";
                            return $"{objExpr}.{mm}()";
                        }
                    }
                }
                // C# Nullable<T>.Value → Java wrapper unbox method (Boolean.booleanValue(), etc.)
                if (prop != null
                    && ExpressionTransformerHelpers.TryGetNullableValueUnboxMethod(prop, context, out var bindingUnboxMethod))
                {
                    return $"{objExpr}.{bindingUnboxMethod}()";
                }

                // No mapping found — generate a default getXxx() getter for property-like names.
                // Names starting with uppercase are likely properties in C# that should be
                // getters in Java.
                if (memberName.Length > 0 && char.IsUpper(memberName[0]))
                {
                    var getter = "get" + memberName;
                    return $"{objExpr}.{getter}()";
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
                        receiverType = context.GetTypeInfo(condParent.Expression).Type;
                    }
                    var streamExpr = Transformers.Expression.Utilities.ExpressionTransformerHelpers
                        .BuildStreamExpression(objExpr, receiverType, context);

                    // Add .collect() terminal when this is the outermost expression
                    bool needsCollect = invocation.Parent is not MemberAccessExpressionSyntax;
                    var terminal = "";
                    if (needsCollect)
                    {
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
                        terminal = ".collect(CSharpList.toCSharpList())";
                    }

                    var result = $"{streamExpr}.{streamOp}({linqArg}){terminal}";
                    // Don't wrap in CSharpGenericIterable.from() here — keep concrete types
                    // for local variables. Return statements handle wrapping separately.
                    return result;
                }

                var invokedTarget = TransformWhenNotNull(invocation.Expression, objExpr, context);
                var invArgs = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => Transform(a.Expression, context)));
                return $"{invokedTarget}({invArgs})";

            case MemberAccessExpressionSyntax memberAccess:
                var accessTarget = TransformWhenNotNull(memberAccess.Expression, objExpr, context);
                var rawName = memberAccess.Name.Identifier.Text;

                // C# Type.FullName → TypeHelper.getFullName(type) because java.lang.Class
                // has no direct equivalent that matches C# semantics for arrays/primitives.
                if (rawName == "FullName" && IsSystemTypeExpression(memberAccess.Expression, context))
                {
                    context.AddImport("io.github.ningpp.compat.TypeHelper");
                    return $"TypeHelper.getFullName({accessTarget})";
                }

                // Property access in a conditional-access chain must still be converted to
                // Java bean getters (e.g. choice?.Mapping.TypeDesc.FullName).
                if (context.GetSymbolInfo(memberAccess).Symbol is IPropertySymbol memberProp)
                {
                    if (ExpressionTransformerHelpers.TryGetNullableValueUnboxMethod(memberProp, context, out var memberUnboxMethod))
                        return $"{accessTarget}.{memberUnboxMethod}()";

                    var memberExprType = context.GetTypeInfo(memberAccess.Expression).Type;
                    var memberMapped = TryMapPropertyToMethod(memberProp, memberExprType, context);
                    if (memberMapped != null)
                    {
                        if (ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(memberMapped))
                            return $"{memberMapped}()";
                        if (memberMapped.Contains('.')) return memberMapped;
                        if (memberProp.ContainingType?.SpecialType == SpecialType.System_Array)
                            return $"{accessTarget}.{memberMapped}";
                        return $"{accessTarget}.{memberMapped}()";
                    }

                    var memberGetter = "get" + char.ToUpperInvariant(rawName[0]) + rawName[1..];
                    return $"{accessTarget}.{memberGetter}()";
                }

                // C# PascalCase methods → Java camelCase: when used as an invocation
                // (parent is InvocationExpressionSyntax), lowercase the first letter.
                if (memberAccess.Parent is InvocationExpressionSyntax && rawName.Length > 0)
                    rawName = char.ToLowerInvariant(rawName[0]) + rawName[1..];
                return $"{accessTarget}.{ConversionContext.EscapeJavaKeyword(rawName)}";

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
    /// Checks the expression type first (for concrete-type overrides), then the property's
    /// containing type, then its FQN, then walks all implemented interfaces.
    /// Returns null when no mapping is found.
    /// </summary>
    private static string? TryMapPropertyToMethod(IPropertySymbol prop, ITypeSymbol? exprType, ConversionContext context)
    {
        // Check expression's static type first (e.g., SortedList before IDictionary)
        if (exprType != null && !SymbolEqualityComparer.Default.Equals(exprType, prop.ContainingType))
        {
            var exprTypeName = exprType.ToDisplayString();
            var exprMapped = context.TypeMappings.MapMethod(exprTypeName, prop.Name);
            if (exprMapped == null)
            {
                var exprFqn = $"{exprType.ContainingNamespace}.{exprType.Name}";
                exprMapped = context.TypeMappings.MapMethod(exprFqn, prop.Name);
            }
            if (exprMapped != null) return exprMapped;
        }

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
        Transformers.Expression.Utilities.ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mapped, context);
        return mapped;
    }

    /// <summary>
    /// Maps a C# method name to its Java equivalent for null-conditional member bindings.
    /// Applies well-known renames (ToString→toString, GetHashCode→hashCode, etc.)
    /// and falls back to camelCase. Does NOT append parentheses — the parent
    /// InvocationExpressionSyntax case adds them.
    /// </summary>
    private static string ApplyMethodBindingNameMapping(IMethodSymbol? method, string memberName, ConversionContext context)
    {
        // Try TypeMappings via semantic model first
        if (method != null)
        {
            var typeName = method.ContainingType.ToDisplayString();
            var mapped = context.TypeMappings.MapMethod(typeName, memberName);
            if (mapped != null)
            {
                Transformers.Expression.Utilities.ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mapped, context);
                return ConversionContext.EscapeJavaKeyword(mapped);
            }

            var fqn = $"{method.ContainingType.ContainingNamespace}.{method.ContainingType.Name}";
            mapped = context.TypeMappings.MapMethod(fqn, memberName);
            if (mapped != null)
            {
                Transformers.Expression.Utilities.ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mapped, context);
                return ConversionContext.EscapeJavaKeyword(mapped);
            }
        }

        // Apply well-known renames + camelCase (same logic as InvocationExpressionTransformer.ApplyCamelCaseAndMappings)
        var result = memberName switch
        {
            "ToString"         => "toString",
            "GetHashCode"      => "hashCode",
            "GetEnumerator"    => "iterator",
            "GetType"          => "getClass",
            "Dispose"          => "close",
            "ToLower"          => "toLowerCase",
            "ToUpper"          => "toUpperCase",
            "ToLowerInvariant" => "toLowerCase",
            "ToUpperInvariant" => "toUpperCase",
            _ when memberName.Length > 0
                               => char.ToLowerInvariant(memberName[0]) + memberName[1..],
            _ => memberName
        };

        return ConversionContext.EscapeJavaKeyword(result);
    }

    /// <summary>
    /// Returns true when the expression's type is System.Type (or a subtype such as TypeInfo),
    /// which in Java is represented by java.lang.Class.
    /// </summary>
    private static bool IsSystemTypeExpression(ExpressionSyntax expression, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return false;

        var typeInfo = context.GetTypeInfo(expression);
        var type = typeInfo.Type ?? typeInfo.ConvertedType;
        if (type == null)
            return false;

        var display = type.ToDisplayString();
        return display == "System.Type"
            || display == "System.Reflection.TypeInfo"
            || type.BaseType?.ToDisplayString() == "System.Type";
    }
}
