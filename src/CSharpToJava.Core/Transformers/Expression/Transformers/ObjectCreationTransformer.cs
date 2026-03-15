using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Collections.Generic;
using System.Text;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles object and array creation expressions.
/// </summary>
public class ObjectCreationTransformer : IExpressionTransformer
{
    static ObjectCreationTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.ImplicitObjectCreationExpression,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.AnonymousObjectCreationExpression,
            SyntaxKind.ArrayCreationExpression,
            SyntaxKind.ImplicitArrayCreationExpression,
            SyntaxKind.ArrayInitializerExpression
        }, new ObjectCreationTransformer());
    }

    private static readonly Lazy<ObjectCreationTransformer> _instance = new(() => new());
    public static ObjectCreationTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.ImplicitObjectCreationExpression => TransformNew((ImplicitObjectCreationExpressionSyntax)node, context),
            SyntaxKind.ObjectCreationExpression => TransformObjectCreation((ObjectCreationExpressionSyntax)node, context),
            SyntaxKind.AnonymousObjectCreationExpression => TransformAnonymousObjectCreation((AnonymousObjectCreationExpressionSyntax)node, context),
            SyntaxKind.ArrayCreationExpression => TransformArrayCreation((ArrayCreationExpressionSyntax)node, context),
            SyntaxKind.ImplicitArrayCreationExpression => TransformImplicitArrayCreation((ImplicitArrayCreationExpressionSyntax)node, context),
            SyntaxKind.ArrayInitializerExpression => TransformArrayInitializer((InitializerExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Object creation kind {node.Kind()} not supported.")
        };

    private string TransformNew(ImplicitObjectCreationExpressionSyntax node, ConversionContext context)
    {
        // C# 9+ target-typed new: new() → inferred type
        // We need to infer the type from context or use object
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            var typeName = context.MapType(typeInfo.Value.Type);
            return TransformObjectCreationWithArgs(typeName, node.ArgumentList, context);
        }
        return "new Object()";
    }

    private string TransformObjectCreation(ObjectCreationExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Get the type being created
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        string typeName;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            typeName = context.MapType(typeInfo.Value.Type);
        }
        else
        {
            // Fallback to syntax type
            var typeSyntax = node.Type as TypeSyntax;
            typeName = typeSyntax != null
                ? context.MapTypeFromSyntax(typeSyntax)
                : "Object";
        }

        // Check if there's an object initializer
        if (node.Initializer != null && node.Initializer.Kind() == SyntaxKind.ObjectInitializerExpression)
        {
            return TransformObjectCreationWithInitializer(typeName, node.ArgumentList, node.Initializer, context);
        }

        return TransformObjectCreationWithArgs(typeName, node.ArgumentList, context);
    }

    private string TransformObjectCreationWithArgs(string typeName, ArgumentListSyntax? argumentList, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var args = new List<string>();

        if (argumentList != null)
        {
            foreach (var arg in argumentList.Arguments)
            {
                args.Add(facade.Transform(arg.Expression, context));
            }
        }

        return $"new {typeName}({string.Join(", ", args)})";
    }

    private string TransformObjectCreationWithInitializer(string typeName, ArgumentListSyntax? argumentList, InitializerExpressionSyntax initializer, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var args = new List<string>();

        // Build constructor arguments
        if (argumentList != null)
        {
            foreach (var arg in argumentList.Arguments)
            {
                args.Add(facade.Transform(arg.Expression, context));
            }
        }

        // For Java, we need to create the object, then set properties
        // Since Java doesn't have object initializers, we'll use double-brace initialization

        var initializers = new List<string>();

        foreach (var expr in initializer.Expressions)
        {
            if (expr is AssignmentExpressionSyntax assignExpr)
            {
                var value = facade.Transform(assignExpr.Right, context);

                // Convert property assignment to setter call
                if (assignExpr.Left is MemberAccessExpressionSyntax memberAccess)
                {
                    var propertyName = ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text);
                    var setterName = ConvertToSetter(propertyName);
                    initializers.Add($"this.{setterName}({value})");
                }
                else
                {
                    var target = facade.Transform(assignExpr.Left, context);
                    initializers.Add($"this.{target} = {value}");
                }
            }
        }

        // Java double-brace initialization pattern
        if (initializers.Count > 0)
        {
            var initStr = string.Join("; ", initializers);
            // The outer braces create an anonymous class, the inner braces are instance initializer
            return $"new {typeName}() {{ {{ {initStr}; }} }}";
        }

        return $"new {typeName}({string.Join(", ", args)})";
    }

    private static string ConvertToSetter(string propertyName)
    {
        // Convert property name to setter name (X → setX, Name → setName)
        if (string.IsNullOrEmpty(propertyName)) return "set";

        // If already starts with "set", return as is
        if (propertyName.StartsWith("set", StringComparison.Ordinal)) return propertyName;

        return "set" + char.ToUpper(propertyName[0]) + propertyName.Substring(1);
    }

    private string TransformAnonymousObjectCreation(AnonymousObjectCreationExpressionSyntax node, ConversionContext context)
    {
        // C# anonymous objects have no direct Java equivalent
        // Convert to a Map or emit a placeholder
        context.Diagnostics.Warning("Anonymous object creation has no direct Java equivalent - using Map", node.GetLocation());
        context.AddImport("java.util.HashMap");
        context.AddImport("java.util.Map");

        var facade = ExpressionTransformerFacade.Instance;
        var initializers = new List<string>();

        foreach (var member in node.Initializers)
        {
            if (member.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var key = ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text);
                var value = facade.Transform(memberAccess.Expression, context);
                initializers.Add($"map.put(\"{key}\", {value});");
            }
            else if (member.NameEquals != null)
            {
                var key = ConversionContext.EscapeJavaKeyword(member.NameEquals.Name.Identifier.Text);
                var value = facade.Transform(member.Expression, context);
                initializers.Add($"map.put(\"{key}\", {value});");
            }
        }

        var sb = new StringBuilder();
        sb.Append("(() {\n");
        sb.Append("  Map<String, Object> map = new HashMap<>();\n");
        foreach (var init in initializers)
        {
            sb.Append("  ").Append(init).Append("\n");
        }
        sb.Append("  return map;\n");
        sb.Append("})()");

        return sb.ToString();
    }

    private string TransformArrayCreation(ArrayCreationExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Get the element type
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        string elementType;
        if (typeInfo.HasValue && typeInfo.Value.Type is IArrayTypeSymbol arrayType)
        {
            elementType = context.MapType(arrayType.ElementType);
        }
        else
        {
            // Fallback: parse from syntax
            var typeStr = node.Type?.ElementType?.ToString() ?? "Object";
            elementType = context.MapTypeFromSyntax(node.Type!.ElementType);
        }

        // Get dimensions
        var sizes = new List<string>();
        if (node.Type?.RankSpecifiers.Count > 0)
        {
            foreach (var rankSpec in node.Type.RankSpecifiers)
            {
                if (rankSpec.Sizes.Count > 0)
                {
                    foreach (var size in rankSpec.Sizes)
                    {
                        sizes.Add(facade.Transform(size, context));
                    }
                }
                else
                {
                    sizes.Add(""); // Empty size for jagged arrays
                }
            }
        }

        // Build array creation string
        var result = new StringBuilder("new ");
        result.Append(elementType);

        // Add brackets for each dimension
        for (int i = 0; i < sizes.Count; i++)
        {
            result.Append("[");
            if (!string.IsNullOrEmpty(sizes[i]))
            {
                result.Append(sizes[i]);
            }
            result.Append("]");
        }

        // Add initializer if present
        if (node.Initializer != null)
        {
            result.Append(TransformArrayInitializer(node.Initializer, context));
        }

        return result.ToString();
    }

    private string TransformImplicitArrayCreation(ImplicitArrayCreationExpressionSyntax node, ConversionContext context)
    {
        // C# new[] { 1, 2, 3 } - type is inferred from elements
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        string elementType;
        if (typeInfo.HasValue && typeInfo.Value.Type is IArrayTypeSymbol arrayType)
        {
            elementType = context.MapType(arrayType.ElementType);
        }
        else
        {
            // Try to infer from first element
            if (node.Initializer?.Expressions.Count > 0)
            {
                var firstTypeInfo = context.SemanticModel?.GetTypeInfo(node.Initializer.Expressions[0]);
                if (firstTypeInfo.HasValue && firstTypeInfo.Value.Type != null)
                {
                    elementType = context.MapType(firstTypeInfo.Value.Type);
                }
                else
                {
                    elementType = "Object";
                }
            }
            else
            {
                elementType = "Object";
            }
        }

        var result = new StringBuilder("new ");
        result.Append(elementType);

        // Add dimension brackets based on the number of commas
        // new[] has 0 commas = 1 dimension
        // new[,] has 1 comma = 2 dimensions
        int dimensions = node.Commas.Count + 1;
        for (int i = 0; i < dimensions; i++)
        {
            result.Append("[]");
        }

        // Add initializer
        if (node.Initializer != null)
        {
            result.Append(TransformArrayInitializer(node.Initializer, context));
        }

        return result.ToString();
    }

    private string TransformArrayInitializer(InitializerExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var values = new List<string>();

        foreach (var expr in node.Expressions)
        {
            values.Add(facade.Transform(expr, context));
        }

        return $" {{ {string.Join(", ", values)} }}";
    }
}
