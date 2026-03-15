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
[TransformerRegistration]
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
            SyntaxKind.ArrayInitializerExpression,
            SyntaxKind.StackAllocArrayCreationExpression
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
            SyntaxKind.StackAllocArrayCreationExpression => TransformStackAlloc((StackAllocArrayCreationExpressionSyntax)node, context),
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

        // Check if there's a collection initializer
        if (node.Initializer != null && node.Initializer.Kind() == SyntaxKind.CollectionInitializerExpression)
        {
            return TransformCollectionCreationWithInitializer(typeName, node.Initializer, context);
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
                args.Add(facade.Transform(arg.Expression, context));
        }

        // Emit the object creation and setter calls as pre-statements, then return the temp var.
        // This avoids the double-brace anonymous-subclass anti-pattern which leaks memory,
        // prevents the type from being final, and breaks equals() checks.
        string tmpVar = context.GenerateSyntheticName("_obj");
        context.AddPreStatement($"var {tmpVar} = new {typeName}({string.Join(", ", args)});");

        foreach (var expr in initializer.Expressions)
        {
            if (expr is AssignmentExpressionSyntax assignExpr)
            {
                var value = facade.Transform(assignExpr.Right, context);

                if (assignExpr.Left is IdentifierNameSyntax idName)
                {
                    var propertyName = ConversionContext.EscapeJavaKeyword(idName.Identifier.Text);
                    var setterName = ConvertToSetter(propertyName);
                    context.AddPreStatement($"{tmpVar}.{setterName}({value});");
                }
                else if (assignExpr.Left is MemberAccessExpressionSyntax memberAccess)
                {
                    var propertyName = ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text);
                    var setterName = ConvertToSetter(propertyName);
                    context.AddPreStatement($"{tmpVar}.{setterName}({value});");
                }
                else
                {
                    var target = facade.Transform(assignExpr.Left, context);
                    context.AddPreStatement($"{tmpVar}.{target} = {value};");
                }
            }
        }

        return tmpVar;
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
        // C# anonymous objects map to Map<String, Object> in Java
        context.AddImport("java.util.Map");
        var facade = ExpressionTransformerFacade.Instance;

        var pairs = new List<(string key, string value)>();
        foreach (var member in node.Initializers)
        {
            string key, value;
            if (member.NameEquals != null)
            {
                key = ConversionContext.EscapeJavaKeyword(member.NameEquals.Name.Identifier.Text);
                value = facade.Transform(member.Expression, context);
            }
            else if (member.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                key = ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text);
                value = facade.Transform(member.Expression, context);
            }
            else
            {
                key = $"_field{pairs.Count}";
                value = facade.Transform(member.Expression, context);
            }
            pairs.Add((key, value));
        }

        // Map.of() supports up to 10 entries; use it for small objects
        if (pairs.Count <= 10)
        {
            var entries = string.Join(", ", pairs.Select(p => $"\"{p.key}\", {p.value}"));
            return pairs.Count == 0 ? "Map.of()" : $"Map.of({entries})";
        }

        // For larger objects use a Supplier lambda to stay as expression
        context.AddImport("java.util.HashMap");
        var sb = new StringBuilder();
        sb.Append("((java.util.function.Supplier<Map<String, Object>>) () -> {\n");
        sb.Append("    Map<String, Object> _map = new HashMap<>();\n");
        foreach (var (k, v) in pairs)
            sb.Append($"    _map.put(\"{k}\", {v});\n");
        sb.Append("    return _map;\n");
        sb.Append("}).get()");
        return sb.ToString();
    }

    private string TransformArrayCreation(ArrayCreationExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Fix: Always use SYNTAX element type to get the innermost type (e.g. double for double[][]).
        // Using the semantic model's ElementType for double[][] gives double[] (nested), which
        // would produce double[][n][] instead of the correct double[n][].
        string elementType = context.MapTypeFromSyntax(node.Type.ElementType);

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
                        // Fix: OmittedArraySizeExpression (from e.g. new T[n][])
                        // should produce an empty bracket [], not a TODO comment.
                        if (size.IsKind(SyntaxKind.OmittedArraySizeExpression))
                            sizes.Add(""); // empty — Java uses [] for unspecified dimensions
                        else
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

    /// <summary>
    /// Handles collection initializers: new List&lt;T&gt; { ... }, new HashSet&lt;T&gt; { ... },
    /// new Dictionary&lt;K,V&gt; { { k, v }, ... }.
    /// </summary>
    private string TransformCollectionCreationWithInitializer(
        string typeName, InitializerExpressionSyntax initializer, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Dictionary-like: each element is a ComplexElementInitializerExpression ({ key, value })
        bool isDictionaryLike = initializer.Expressions.Count > 0 &&
            initializer.Expressions.All(e => e.Kind() == SyntaxKind.ComplexElementInitializerExpression);

        if (isDictionaryLike)
        {
            string tmpVar = context.GenerateSyntheticName("_map");
            context.AddPreStatement($"var {tmpVar} = new {typeName}();");
            foreach (var element in initializer.Expressions)
            {
                if (element is InitializerExpressionSyntax complexInit && complexInit.Expressions.Count == 2)
                {
                    var key = facade.Transform(complexInit.Expressions[0], context);
                    var value = facade.Transform(complexInit.Expressions[1], context);
                    context.AddPreStatement($"{tmpVar}.put({key}, {value});");
                }
            }
            return tmpVar;
        }

        // Set-like or List-like: emit as a single constructor expression
        var items = initializer.Expressions.Select(e => facade.Transform(e, context)).ToList();
        var itemsStr = string.Join(", ", items);

        bool isSetLike = typeName.StartsWith("HashSet", StringComparison.Ordinal) ||
                         typeName.StartsWith("TreeSet", StringComparison.Ordinal) ||
                         typeName.StartsWith("LinkedHashSet", StringComparison.Ordinal);

        if (isSetLike)
        {
            if (context.Options.TargetJavaVersion >= JavaVersion.Java11)
            {
                context.AddImport("java.util.Set");
                return $"new {typeName}(Set.of({itemsStr}))";
            }
            context.AddImport("java.util.Arrays");
            return $"new {typeName}(Arrays.asList({itemsStr}))";
        }

        // Default: List-like
        context.AddImport("java.util.Arrays");
        return $"new {typeName}(Arrays.asList({itemsStr}))";
    }

    /// <summary>
    /// Maps C# stackalloc to a Java heap allocation with an explanatory comment.
    /// stackalloc has no Java equivalent; semantics differ (stack vs heap) but behavior is equivalent.
    /// </summary>
    private string TransformStackAlloc(StackAllocArrayCreationExpressionSyntax node, ConversionContext context)
    {
        // stackalloc has no Java equivalent; heap-allocate instead
        var facade = ExpressionTransformerFacade.Instance;
        // node.Type is TypeSyntax but stackalloc always produces an ArrayTypeSyntax
        var arrayTypeSyntax = (ArrayTypeSyntax)node.Type;
        string elementType = context.MapTypeFromSyntax(arrayTypeSyntax.ElementType);

        var sizes = new List<string>();
        foreach (var rankSpec in arrayTypeSyntax.RankSpecifiers)
        {
            if (rankSpec.Sizes.Count > 0)
            {
                foreach (var size in rankSpec.Sizes)
                    sizes.Add(facade.Transform(size, context));
            }
            else
            {
                sizes.Add("");
            }
        }

        var sb = new StringBuilder("new ");
        sb.Append(elementType);
        foreach (var s in sizes)
        {
            sb.Append('[');
            sb.Append(s);
            sb.Append(']');
        }

        if (node.Initializer != null)
            sb.Append(TransformArrayInitializer(node.Initializer, context));

        return $"/* C# stackalloc — allocated on heap in Java */ {sb}";
    }
}
