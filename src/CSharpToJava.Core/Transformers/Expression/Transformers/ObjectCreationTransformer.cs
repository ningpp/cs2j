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
        if (argumentList == null || argumentList.Arguments.Count == 0)
            return $"new {typeName}()";

        // Resolve the constructor symbol so CoerceArgumentType can insert narrowing casts
        // (e.g. byte/short parameters receiving int literals require an explicit Java cast).
        IMethodSymbol? ctorSymbol = null;
        if (context.SemanticModel != null && argumentList.Parent != null)
        {
            var symInfo = context.SemanticModel.GetSymbolInfo(argumentList.Parent);
            ctorSymbol = symInfo.Symbol as IMethodSymbol;
        }

        var args = ArgumentTransformer.TransformArgumentList(
            argumentList, context, ExpressionTransformerFacade.Instance, methodSymbol: ctorSymbol);

        // Fallback: when the constructor symbol could not be resolved (ctorSymbol is null),
        // CoerceArgumentType never fires for the arguments, so array→Collection coercion is skipped.
        // If the target type is a Java collection (ArrayList, HashSet, etc.) and any argument is an
        // array, we must wrap it here because Java arrays are not Collection subtypes.
        if (ctorSymbol == null && context.SemanticModel != null && IsJavaCollectionType(typeName))
        {
            args = CoerceArrayArgsForCollectionCtor(
                argumentList.Arguments, args, context);
        }

        return $"new {typeName}({args})";
    }

    /// <summary>
    /// Returns true when <paramref name="typeName"/> is a Java concrete collection class whose
    /// constructor accepts a <c>Collection</c> parameter (e.g. ArrayList, HashSet, TreeSet …).
    /// </summary>
    private static bool IsJavaCollectionType(string typeName)
    {
        // Strip generic parameters (e.g. "ArrayList<String>" → "ArrayList")
        var bare = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
        return bare is "ArrayList" or "HashSet" or "TreeSet" or "LinkedList"
            or "ArrayDeque" or "LinkedHashSet" or "PriorityQueue" or "Stack" or "Vector";
    }

    /// <summary>
    /// When the constructor symbol is unavailable, scan each argument for array types.
    /// Any array argument passed to a Java collection constructor must be wrapped:
    ///   • reference-type arrays  → Arrays.asList(expr)
    ///   • primitive arrays       → Arrays.stream(expr).boxed().collect(Collectors.toList())
    /// Returns the updated comma-separated argument string.
    /// </summary>
    private static string CoerceArrayArgsForCollectionCtor(
        SeparatedSyntaxList<ArgumentSyntax> syntaxArgs,
        string transformedArgs,
        ConversionContext context)
    {
        // Re-transform each argument individually so we can wrap array ones.
        var parts = new List<string>();
        bool changed = false;
        var argList = syntaxArgs.ToList();
        var rawParts = SplitTopLevelArgs(transformedArgs);

        for (int i = 0; i < argList.Count && i < rawParts.Count; i++)
        {
            var arg = argList[i];
            var expr = rawParts[i];

            var argType = context.SemanticModel!.GetTypeInfo(arg.Expression).Type;
            if (argType is IArrayTypeSymbol arrayType)
            {
                expr = WrapArrayForCollectionArg(expr, arrayType, context);
                changed = true;
            }
            parts.Add(expr);
        }

        return changed ? string.Join(", ", parts) : transformedArgs;
    }

    /// <summary>
    /// Wraps an array expression so it is compatible with a Java Collection parameter.
    /// </summary>
    internal static string WrapArrayForCollectionArg(string expr, IArrayTypeSymbol arrayType, ConversionContext context)
    {
        if (arrayType.ElementType.IsValueType && IsPrimitiveSpecialType(arrayType.ElementType.SpecialType))
        {
            context.AddImport("java.util.Arrays");
            context.AddImport("java.util.stream.Collectors");
            return $"java.util.Arrays.stream({expr}).boxed().collect(java.util.stream.Collectors.toList())";
        }

        context.AddImport("java.util.Arrays");
        return $"Arrays.asList({expr})";
    }

    private static bool IsPrimitiveSpecialType(SpecialType st)
        => st is SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
            or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Single
            or SpecialType.System_Boolean or SpecialType.System_Char;

    /// <summary>
    /// Splits a comma-separated argument string at the top level (ignoring commas inside &lt;&gt;, (), []).
    /// </summary>
    private static List<string> SplitTopLevelArgs(string args)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case '<': case '(': case '[': depth++; break;
                case '>': case ')': case ']': depth--; break;
                case ',' when depth == 0:
                    result.Add(args[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        result.Add(args[start..].Trim());
        return result;
    }

    private string TransformObjectCreationWithInitializer(string typeName, ArgumentListSyntax? argumentList, InitializerExpressionSyntax initializer, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Build constructor arguments (with narrowing-cast coercion via ArgumentTransformer)
        string ctorArgs = "";
        if (argumentList != null && argumentList.Arguments.Count > 0)
        {
            IMethodSymbol? ctorSymbol = null;
            if (context.SemanticModel != null && argumentList.Parent != null)
            {
                var symInfo = context.SemanticModel.GetSymbolInfo(argumentList.Parent);
                ctorSymbol = symInfo.Symbol as IMethodSymbol;
            }
            ctorArgs = ArgumentTransformer.TransformArgumentList(
                argumentList, context, facade, methodSymbol: ctorSymbol);
        }

        // Emit the object creation and setter calls as pre-statements, then return the temp var.
        // This avoids the double-brace anonymous-subclass anti-pattern which leaks memory,
        // prevents the type from being final, and breaks equals() checks.
        string tmpVar = context.GenerateSyntheticName("_obj");
        context.AddPreStatement($"var {tmpVar} = new {typeName}({ctorArgs});");

        foreach (var expr in initializer.Expressions)
        {
            if (expr is AssignmentExpressionSyntax assignExpr)
            {
                var value = facade.Transform(assignExpr.Right, context);

                if (assignExpr.Left is IdentifierNameSyntax idName)
                {
                    // Use semantic model to distinguish public fields from properties:
                    // public fields → direct Java field assignment (e.g. _obj.Left = value)
                    // properties → setter method call (e.g. _obj.setLeft(value))
                    var memberSymbol = context.SemanticModel?.GetSymbolInfo(idName).Symbol;
                    if (memberSymbol is IFieldSymbol fieldSym)
                    {
                        var javaFieldName = ConversionContext.EscapeJavaKeyword(fieldSym.Name);
                        context.AddPreStatement($"{tmpVar}.{javaFieldName} = {value};");
                    }
                    else
                    {
                        var propertyName = ConversionContext.EscapeJavaKeyword(idName.Identifier.Text);
                        var setterName = ConvertToSetter(propertyName);
                        context.AddPreStatement($"{tmpVar}.{setterName}({value});");
                    }
                }
                else if (assignExpr.Left is MemberAccessExpressionSyntax memberAccess)
                {
                    var memberSymbol2 = context.SemanticModel?.GetSymbolInfo(memberAccess.Name).Symbol;
                    if (memberSymbol2 is IFieldSymbol fieldSym2)
                    {
                        var javaFieldName = ConversionContext.EscapeJavaKeyword(fieldSym2.Name);
                        context.AddPreStatement($"{tmpVar}.{javaFieldName} = {value};");
                    }
                    else
                    {
                        var propertyName = ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text);
                        var setterName = ConvertToSetter(propertyName);
                        context.AddPreStatement($"{tmpVar}.{setterName}({value});");
                    }
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
        var facade = ExpressionTransformerFacade.Instance;

        // When targeting Java 17+ with records enabled, synthesize a Java record instead of Map
        if (context.Options.UseRecords && context.Options.TargetJavaVersion >= JavaVersion.Java17)
        {
            var (record, ctorCall) = AnonymousTypeRecordSynthesizer.SynthesizeForAnonymousType(
                node, context, expr => facade.Transform(expr, context));
            return ctorCall;
        }

        // Fallback: C# anonymous objects map to Map<String, Object> in Java
        context.AddImport("java.util.Map");

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

        // Resolve element type via the semantic model when available — this handles generic
        // type mappings through the FQN registry (e.g. List<int[]> → ArrayList<int[]>).
        // We call GetTypeInfo on node.Type.ElementType (the element-type syntax node), NOT on
        // the whole array-creation expression, so for double[][] the element type stays "double"
        // (not "double[]") and rank specifiers carry the remaining dimensions correctly.
        // Fall back to the syntax-based path when the semantic model is unavailable.
        string elementType;
        var elemSemType = context.SemanticModel?.GetTypeInfo(node.Type.ElementType).Type;
        if (elemSemType != null)
            elementType = context.MapType(elemSemType);
        else
            elementType = context.MapTypeFromSyntax(node.Type.ElementType);

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

        // Build array creation string.
        // Java forbids generic array creation (e.g. new ArrayList<T>[n] is illegal due to type
        // erasure). Use the raw type (strip type arguments) in the new-expression only.
        string rawElementType = elementType;
        int genericArgStart = elementType.IndexOf('<');
        if (genericArgStart > 0)
            rawElementType = elementType.Substring(0, genericArgStart);

        // Fix: Java doesn't allow creating arrays of type parameters (e.g., new T[n]).
        // Use (T[]) new Object[n] with an unchecked cast instead.
        bool isTypeParameterArray = elemSemType != null && elemSemType.TypeKind == TypeKind.TypeParameter;

        var result = new StringBuilder();
        if (isTypeParameterArray)
        {
            // For type parameter arrays, use (T[]) new Object[...]
            result.Append('(');
            result.Append(elementType);
            result.Append("[]) new Object");
        }
        else
        {
            result.Append("new ");
            result.Append(rawElementType);
        }

        // Add brackets for each dimension.
        // When an initializer is present, Java forbids explicit sizes (e.g. new double[4]{...}
        // is invalid); emit empty brackets so the initializer provides the length.
        for (int i = 0; i < sizes.Count; i++)
        {
            result.Append("[");
            if (node.Initializer == null && !string.IsNullOrEmpty(sizes[i]))
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
            // Fallback 1: Try the converted type (assignment/declaration context target type)
            var convertedType = typeInfo.HasValue ? typeInfo.Value.ConvertedType : null;
            if (convertedType is IArrayTypeSymbol convertedArray)
            {
                elementType = context.MapType(convertedArray.ElementType);
            }
            // Fallback 2: Try to infer from first element
            else if (node.Initializer?.Expressions.Count > 0)
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

        // When the element type resolved to "Object" and all initializer elements are
        // anonymous-type creations in Java-records mode, synthesize the record and use its name.
        // This turns new Object[] { new A(1), ... } into new A[] { new A(1), ... } so that
        // stream lambdas can call typed accessors (e.g. x.getId()) without cast failures.
        if (elementType == "Object"
            && node.Initializer?.Expressions.Count > 0
            && node.Initializer.Expressions.All(e => e is AnonymousObjectCreationExpressionSyntax)
            && context.Options.UseRecords && context.Options.TargetJavaVersion >= JavaVersion.Java17)
        {
            var facade2 = ExpressionTransformerFacade.Instance;
            var firstAnon = (AnonymousObjectCreationExpressionSyntax)node.Initializer.Expressions[0];
            var (record, _) = AnonymousTypeRecordSynthesizer.SynthesizeForAnonymousType(
                firstAnon, context, expr => facade2.Transform(expr, context));
            elementType = record.RecordName;
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
