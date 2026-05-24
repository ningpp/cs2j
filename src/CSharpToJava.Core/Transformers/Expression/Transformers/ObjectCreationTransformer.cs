using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using CSharpToJava.Core.Transformers.Utilities;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles object and array creation expressions.
/// </summary>
[TransformerRegistration]
public class ObjectCreationTransformer : IIRExpressionTransformer
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

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // ObjectCreationExpression: new Type(args) — handle simple cases as JavaNewExpression
        if (node is ObjectCreationExpressionSyntax objCreation
            && objCreation.Initializer == null)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(node);
            ITypeSymbol? createdType = typeInfo?.Type;

            // Skip special types that need non-standard IR handling
            if (createdType?.TypeKind != TypeKind.Delegate
                && createdType is not ITypeParameterSymbol)
            {
                string typeName = createdType != null
                    ? context.MapType(createdType)
                    : (objCreation.Type is TypeSyntax ts ? context.MapTypeFromSyntax(ts) : "Object");

                if (!NeedsSpecialCreationHandling(typeName, objCreation, context)
                    && !HasComplexArguments(objCreation.ArgumentList))
                {
                    var ir = new JavaNewExpression { Type = typeName };
                    if (objCreation.ArgumentList != null)
                    {
                        foreach (var arg in objCreation.ArgumentList.Arguments)
                            ir.Arguments.Add(facade.TransformToIR(arg.Expression, context));
                    }
                    var runtimeClassArguments = RuntimeClassParameterHelper.GetRuntimeClassArguments(createdType as INamedTypeSymbol, context);
                    foreach (var runtimeClassArgument in runtimeClassArguments)
                        ir.Arguments.Add(new JavaRawExpression(runtimeClassArgument));
                    return ir;
                }
            }
        }

        // ImplicitObjectCreationExpression: new() — target-typed
        if (node is ImplicitObjectCreationExpressionSyntax implicitNew)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(node);
            if (typeInfo?.Type != null)
            {
                var typeName = context.MapType(typeInfo.Value.Type);
                if (!HasComplexArguments(implicitNew.ArgumentList))
                {
                    var ir = new JavaNewExpression { Type = typeName };
                    foreach (var arg in implicitNew.ArgumentList.Arguments)
                        ir.Arguments.Add(facade.TransformToIR(arg.Expression, context));
                    var runtimeClassArguments = RuntimeClassParameterHelper.GetRuntimeClassArguments(typeInfo.Value.Type as INamedTypeSymbol, context);
                    foreach (var runtimeClassArgument in runtimeClassArguments)
                        ir.Arguments.Add(new JavaRawExpression(runtimeClassArgument));
                    return ir;
                }
            }
        }

        // Array creation, anonymous objects, initializers, special types → raw
        return new JavaRawExpression(Transform(node, context));
    }

    /// <summary>
    /// Returns true if the type requires special handling in the string path that would
    /// produce output different from a simple <c>new Type(args)</c>.
    /// </summary>
    private static bool NeedsSpecialCreationHandling(
        string typeName,
        ObjectCreationExpressionSyntax node,
        ConversionContext context)
    {
        var bareTypeName = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;

        // Types remapped to different constructors
        if (bareTypeName is "Map.Entry") return true;

        // Functional interfaces → delegate construction
        if (IsJavaFunctionalInterfaceType(typeName)) return true;
        if (node.ArgumentList?.Arguments.Count == 1)
        {
            var ctorSym = context.SemanticModel?.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (ctorSym?.ContainingType.TypeKind == TypeKind.Delegate) return true;
        }

        // Types with argument reshaping
        if (bareTypeName.EndsWith("LineSegment", StringComparison.Ordinal)) return true;
        if (bareTypeName.EndsWith("BufferedReader", StringComparison.Ordinal)) return true;

        // Java collection types need argument coercion (Arrays.asList wrapping)
        if (IsJavaCollectionType(typeName)) return true;

        return false;
    }

    /// <summary>
    /// Returns true if any argument uses named parameters or ref/out/in keywords.
    /// </summary>
    private static bool HasComplexArguments(ArgumentListSyntax? argList)
    {
        if (argList == null) return false;
        foreach (var arg in argList.Arguments)
        {
            if (arg.NameColon != null) return true;
            if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                || arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                || arg.RefKindKeyword.IsKind(SyntaxKind.InKeyword))
                return true;
        }
        return false;
    }

    private string TransformNew(ImplicitObjectCreationExpressionSyntax node, ConversionContext context)
    {
        // C# 9+ target-typed new: new() → inferred type
        // We need to infer the type from context or use object
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            var typeName = context.MapType(typeInfo.Value.Type);
            return TransformObjectCreationWithArgs(typeName, node.ArgumentList, null, context, typeInfo.Value.Type as INamedTypeSymbol);
        }
        return "new Object()";
    }

    private string TransformObjectCreation(ObjectCreationExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        ITypeSymbol? createdTypeSymbol = null;

        // Get the type being created
        var typeInfo = context.SemanticModel?.GetTypeInfo(node);
        string typeName;
        if (typeInfo.HasValue && typeInfo.Value.Type != null && typeInfo.Value.Type is not IErrorTypeSymbol)
        {
            createdTypeSymbol = typeInfo.Value.Type;
            typeName = context.MapType(typeInfo.Value.Type);
        }
        else
        {
            // Fallback to syntax type.  Prefer syntax over unresolved error types
            // (common in project-pipeline after LINQ rewrite) because error types
            // can lose generic type arguments (e.g. Tuple<int,int> → Tuple → Map.Entry
            // instead of Map.Entry<Integer,Integer>).
            var typeSyntax = node.Type as TypeSyntax;
            typeName = typeSyntax != null
                ? context.MapTypeFromSyntax(typeSyntax)
                : "Object";
        }

        if (createdTypeSymbol?.ToDisplayString() == "System.IO.FileStream")
        {
            context.AddImport("io.github.ningpp.compat.FileHelper");
            var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
            return $"FileHelper.open({args})";
        }

        // Java cannot instantiate a type parameter directly (new T()).
        // For C# where T : ICollection<...>, new() we map to ArrayList and cast.
        // For other new()-constrained type params, keep a compilable fallback cast.
        if ((node.ArgumentList == null || node.ArgumentList.Arguments.Count == 0)
            && createdTypeSymbol is ITypeParameterSymbol typeParameter)
        {
            if (HasCollectionConstraint(typeParameter))
            {
                context.AddImport("java.util.ArrayList");
                return $"({typeName}) new ArrayList<>()";
            }

            if (typeParameter.HasConstructorConstraint)
                return $"({typeName}) new Object()";
        }

        // Check if there's an object initializer
        if (node.Initializer != null && node.Initializer.Kind() == SyntaxKind.ObjectInitializerExpression)
        {
            return TransformObjectCreationWithInitializer(typeName, node.ArgumentList, node.Initializer, context, createdTypeSymbol as INamedTypeSymbol);
        }

        // Check if there's a collection initializer
        if (node.Initializer != null && node.Initializer.Kind() == SyntaxKind.CollectionInitializerExpression)
        {
            return TransformCollectionCreationWithInitializer(typeName, node.Initializer, createdTypeSymbol, context);
        }

        // Delegate construction (new D(expr)) should become a functional value in Java,
        // not interface instantiation.
        if (createdTypeSymbol?.TypeKind == TypeKind.Delegate
            && node.ArgumentList?.Arguments.Count == 1)
        {
            return facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
        }

            return TransformObjectCreationWithArgs(typeName, node.ArgumentList, node.Type as TypeSyntax, context, createdTypeSymbol as INamedTypeSymbol);
    }

    private static bool HasCollectionConstraint(ITypeParameterSymbol typeParameter)
    {
        return typeParameter.ConstraintTypes.Any(ct =>
        {
            if (ct is INamedTypeSymbol named)
            {
                if (named.Name is "ICollection" or "IEnumerable")
                    return true;

                return named.AllInterfaces.Any(i => i.Name is "ICollection" or "IEnumerable");
            }

            return false;
        });
    }

    private string TransformObjectCreationWithArgs(
        string typeName,
        ArgumentListSyntax? argumentList,
        TypeSyntax? typeSyntax,
        ConversionContext context,
        INamedTypeSymbol? createdTypeSymbol = null)
    {
        // Map.Entry is an interface — instantiate via AbstractMap.SimpleEntry instead.
        // This handles C# `new KeyValuePair<K,V>(key, value)` construction.
        // typeName may include generics (e.g. "Map.Entry<Foo, Bar>"), so strip them.
        // When explicit generic type arguments are present, preserve them to avoid
        // generic invariance issues (ArrayList<SimpleEntry<A,B>> ≠ Iterable<Map.Entry<A,B>>).
        // Also cast to Map.Entry<K,V> so the expression's type is the interface, not the
        // concrete class — this is necessary for stream .map() lambdas that feed into
        // generic collections expecting Map.Entry element types.
        var bareTypeName = typeName.Contains('<') ? typeName.Substring(0, typeName.IndexOf('<')) : typeName;
        if (bareTypeName is "Map.Entry")
        {
            context.AddImport("java.util.AbstractMap");
            var seArgs = argumentList != null
                ? ArgumentTransformer.TransformArgumentList(argumentList, context, ExpressionTransformerFacade.Instance)
                : "";
            var genericPart = typeName.Contains('<') ? typeName[typeName.IndexOf('<')..] : "<>";
            // When type arguments are missing (bare "Map.Entry"), try to recover them
            // from the C# syntax node. The semantic model may lose generic type arguments
            // after LINQ rewrite in the project pipeline.
            if (genericPart == "<>" && typeSyntax is GenericNameSyntax genericName)
            {
                var syntaxTypeName = context.MapTypeFromSyntax(genericName);
                if (syntaxTypeName.Contains('<'))
                {
                    typeName = syntaxTypeName;
                    genericPart = typeName[typeName.IndexOf('<')..];
                }
            }
            var newExpr = string.IsNullOrWhiteSpace(seArgs)
                ? $"new AbstractMap.SimpleEntry{genericPart}()"
                : $"new AbstractMap.SimpleEntry{genericPart}({seArgs})";
            // Cast to Map.Entry interface so Java type inference sees the interface type
            // in stream pipelines, avoiding ArrayList<SimpleEntry> vs Iterable<Map.Entry> mismatch.
            // Outer parens ensure correct precedence when member access is chained:
            // ((Map.Entry<K,V>) new SE<K,V>(...)).getKey()  — NOT (Map.Entry<K,V>) new SE<K,V>(...).getKey()
            if (genericPart != "<>")
            {
                context.AddImport("java.util.Map");
                return $"((Map.Entry{genericPart}) {newExpr})";
            }
            return newExpr;
        }

        if (argumentList == null || argumentList.Arguments.Count == 0)
        {
            var runtimeArgs = RuntimeClassParameterHelper.GetRuntimeClassArguments(createdTypeSymbol, context);
            return runtimeArgs.Count == 0
                ? $"new {typeName}()"
                : $"new {typeName}({string.Join(", ", runtimeArgs)})";
        }

        // Resolve constructor/delegate symbol early so delegate construction can be handled
        // as a functional value assignment instead of Java object instantiation.
        IMethodSymbol? ctorSymbol = null;
        if (context.SemanticModel != null && argumentList.Parent != null)
        {
            var symInfo = context.SemanticModel.GetSymbolInfo(argumentList.Parent);
            ctorSymbol = symInfo.Symbol as IMethodSymbol;
        }

        // Java functional interfaces are not instantiated with constructors.
        // C# delegate construction like new Func<T, R>(obj.Method) should map to method refs/lambdas.
        if (argumentList.Arguments.Count == 1
            && (IsJavaFunctionalInterfaceType(typeName)
                || ctorSymbol?.ContainingType.TypeKind == TypeKind.Delegate))
        {
            return ExpressionTransformerFacade.Instance.Transform(argumentList.Arguments[0].Expression, context);
        }

        var args = ArgumentTransformer.TransformArgumentList(
            argumentList, context, ExpressionTransformerFacade.Instance, methodSymbol: ctorSymbol);

        if (ctorSymbol == null)
            AppendRuntimeClassArguments(createdTypeSymbol, context, ref args);

        if (ctorSymbol != null
            && ctorSymbol.ContainingType.Name == "Rectangle"
            && ctorSymbol.Parameters.Length == 1
            && ctorSymbol.Parameters[0].Type is INamedTypeSymbol pType
            && pType.Name == "IEnumerable"
            && pType.TypeArguments.Length == 1
            && pType.TypeArguments[0].Name == "Rectangle")
        {
            return "Rectangle.createFrom_Iterable_Rectangle(" + args + ")";
        }

        if (ctorSymbol == null && argumentList.Arguments.Count == 1 && typeName.EndsWith("Rectangle", StringComparison.Ordinal))
        {
            var argType = context.SemanticModel?.GetTypeInfo(argumentList.Arguments[0].Expression).Type as INamedTypeSymbol;
            if (argType != null
                && argType.Name == "IEnumerable"
                && argType.TypeArguments.Length == 1
                && argType.TypeArguments[0].Name == "Rectangle")
            {
                return "Rectangle.createFrom_Iterable_Rectangle(" + args + ")";
            }
        }

        // C# StreamReader/TextReader patterns used to be mapped to BufferedReader with
        // a string path. Keep the adapter for that legacy mapping only.
        if (argumentList.Arguments.Count == 1 && typeName.EndsWith("BufferedReader", StringComparison.Ordinal))
        {
            var argType = context.SemanticModel?.GetTypeInfo(argumentList.Arguments[0].Expression).Type;
            if (argType?.SpecialType == SpecialType.System_String)
            {
                var pathArg = ExpressionTransformerFacade.Instance.Transform(argumentList.Arguments[0].Expression, context);
                context.AddImport("java.io.FileReader");
                return $"new {typeName}(new FileReader({pathArg}))";
            }
        }

        // C# ArgumentOutOfRangeException(paramName, message) is commonly mapped to
        // IllegalArgumentException in Java, but Java has no (String, String) constructor.
        // Fold to a single message: "paramName: message".
        if (typeName == "IllegalArgumentException"
            && argumentList.Arguments.Count == 2
            && context.SemanticModel != null)
        {
            var argTypes = argumentList.Arguments
                .Select(a => context.SemanticModel.GetTypeInfo(a.Expression).Type)
                .ToList();
            if (argTypes.All(t => t?.SpecialType == SpecialType.System_String))
            {
                var parts = SplitTopLevelArgs(args);
                if (parts.Count == 2)
                    args = $"{parts[0]} + \": \" + {parts[1]}";
            }
        }

        if (IsJavaCollectionType(typeName))
        {
            // Always enforce array->Collection wrapping for Java collection constructors.
            // Some semantic paths can miss this coercion even when ctorSymbol resolves.
            if (context.SemanticModel != null)
            {
                args = CoerceArrayArgsForCollectionCtor(
                    argumentList.Arguments, args, context);
            }

            args = CoerceSingleStreamArgForCollectionCtor(argumentList.Arguments, args, context);

            // When the single constructor argument is not a Java Collection (e.g. RbTree,
            // which implements Iterable but not Collection), wrap with stream materialization.
            if (argumentList.Arguments.Count == 1 && context.SemanticModel != null)
            {
                var singleArg = argumentList.Arguments[0].Expression;
                var argType = context.SemanticModel.GetTypeInfo(singleArg).Type;
                if (argType != null
                    && argType is not IArrayTypeSymbol
                    && !IsCSharpCollectionType(argType)
                    && argType.AllInterfaces.Any(i =>
                        i.OriginalDefinition.ToDisplayString() is "System.Collections.IEnumerable"
                            or "System.Collections.Generic.IEnumerable<T>"))
                {
                    context.AddImport("java.util.stream.StreamSupport");
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                    args = $"StreamSupport.stream({args}.spliterator(), false).collect(Collectors.toCollection(() -> new ArrayList<>()))";
                }
            }
        }

        // Java StringWriter has no constructor accepting Locale/CultureInfo.
        // Strip CultureInfo/IFormatProvider arguments from the constructor call.
        var bareType = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
        if ((bareType == "StringWriter" || bareType == "java.io.StringWriter")
            && argumentList.Arguments.Count > 0)
        {
            var filteredArgs = new List<ArgumentSyntax>();
            foreach (var arg in argumentList.Arguments)
            {
                var argType = context.SemanticModel?.GetTypeInfo(arg.Expression).Type;
                var argTypeDisplay = argType?.ToDisplayString();
                if (argTypeDisplay != "System.Globalization.CultureInfo"
                    && argTypeDisplay != "System.IFormatProvider")
                {
                    filteredArgs.Add(arg);
                }
            }
            if (filteredArgs.Count < argumentList.Arguments.Count)
            {
                if (filteredArgs.Count == 0)
                    return "new StringWriter()";
                args = ArgumentTransformer.TransformArgumentList(
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(filteredArgs)),
                    context, ExpressionTransformerFacade.Instance, methodSymbol: ctorSymbol);
            }
        }

        return $"new {typeName}({args})";
    }

    private static void AppendRuntimeClassArguments(
        INamedTypeSymbol? createdTypeSymbol,
        ConversionContext context,
        ref string args)
    {
        var runtimeArgs = RuntimeClassParameterHelper.GetRuntimeClassArguments(createdTypeSymbol, context);
        if (runtimeArgs.Count == 0)
            return;

        var runtimeArgsText = string.Join(", ", runtimeArgs);
        args = string.IsNullOrWhiteSpace(args)
            ? runtimeArgsText
            : args + ", " + runtimeArgsText;
    }

    private static bool IsJavaFunctionalInterfaceType(string typeName)
    {
        var bare = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
        return bare is "Function" or "BiFunction" or "Consumer" or "BiConsumer"
            or "Predicate" or "Supplier" or "Runnable" or "Comparator"
            or "java.util.function.Function" or "java.util.function.BiFunction"
            or "java.util.function.Consumer" or "java.util.function.BiConsumer"
            or "java.util.function.Predicate" or "java.util.function.Supplier"
            or "java.lang.Runnable" or "java.util.Comparator";
    }

    /// <summary>
    /// Returns true when <paramref name="typeName"/> is a Java concrete collection class whose
    /// constructor accepts a <c>Collection</c> parameter (e.g. ArrayList, HashSet, TreeSet …).
    /// </summary>
    private static bool IsJavaCollectionType(string typeName)
    {
        // Strip generic parameters (e.g. "ArrayList<String>" → "ArrayList")
        var bare = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
        if (bare.Contains('.', StringComparison.Ordinal))
            bare = bare[(bare.LastIndexOf('.') + 1)..];
        return bare is "ArrayList" or "HashSet" or "TreeSet" or "LinkedList"
            or "ArrayDeque" or "LinkedHashSet" or "PriorityQueue" or "Stack";
    }

    /// Returns true if the C# type implements ICollection/ICollection<T>.
    private static bool IsCSharpCollectionType(ITypeSymbol type)
    {
        var originalDisplay = type.OriginalDefinition.ToDisplayString();
        return originalDisplay is "System.Collections.Generic.ICollection<T>"
            or "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.ISet<T>"
            or "System.Collections.Generic.IDictionary<TKey,TValue>"
            || type.AllInterfaces.Any(i =>
                i.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.ICollection<T>"
                    or "System.Collections.Generic.IList<T>");
    }

    /// <summary>
    /// When the constructor symbol is unavailable, scan each argument for array types.
    /// Any array argument passed to a Java collection constructor must be wrapped:
    ///   • reference-type arrays  → ArrayHelper.toList(expr)
    ///   • primitive arrays       → Arrays.stream(expr).boxed().collect(Collectors.toCollection(() -> new ArrayList<>()))
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
            if (argType is not IArrayTypeSymbol)
            {
                var symbol = context.SemanticModel.GetSymbolInfo(arg.Expression).Symbol;
                argType = symbol switch
                {
                    IPropertySymbol prop => prop.Type,
                    IFieldSymbol field => field.Type,
                    ILocalSymbol local => local.Type,
                    IParameterSymbol param => param.Type,
                    _ => argType
                };
            }

            if (argType is IArrayTypeSymbol arrayType)
            {
                if (!IsArrayAlreadyWrappedForCollectionArg(expr))
                {
                    expr = WrapArrayForCollectionArg(expr, arrayType, context);
                    changed = true;
                }
            }
            else if (LooksLikeArrayMemberAccess(arg.Expression) && !IsArrayAlreadyWrappedForCollectionArg(expr))
            {
                context.AddImport("io.github.ningpp.compat.ArrayHelper");
                expr = $"ArrayHelper.toList({expr})";
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
        return ExpressionTransformerHelpers.BuildArrayToCollectionExpression(expr, arrayType, context);
    }

    private static bool IsPrimitiveSpecialType(SpecialType st)
        => st is SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
            or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Single
            or SpecialType.System_Boolean or SpecialType.System_Char;

    private static string CoerceSingleStreamArgForCollectionCtor(
        SeparatedSyntaxList<ArgumentSyntax> syntaxArgs,
        string transformedArgs,
        ConversionContext context)
    {
        if (syntaxArgs.Count != 1)
            return transformedArgs;

        var expr = transformedArgs.Trim();
        if (!LooksLikeUnmaterializedStream(expr))
            return transformedArgs;

        context.AddImport("java.util.stream.Collectors");
        context.AddImport("java.util.ArrayList");
        return $"{expr}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
    }

    private static bool IsArrayAlreadyWrappedForCollectionArg(string expr)
    {
        var trimmed = expr.Trim();
        return trimmed.StartsWith("ArrayHelper.toList(", StringComparison.Ordinal)
            || trimmed.StartsWith("Arrays.asList(", StringComparison.Ordinal)
            || trimmed.StartsWith("java.util.Arrays.asList(", StringComparison.Ordinal)
            || trimmed.StartsWith("Arrays.stream(", StringComparison.Ordinal)
            || trimmed.StartsWith("IntStream.range(", StringComparison.Ordinal);
    }

    private static bool LooksLikeArrayMemberAccess(ExpressionSyntax expression)
    {
        if (expression is MemberAccessExpressionSyntax memberAccess)
            return memberAccess.Name.Identifier.Text.EndsWith("Array", StringComparison.Ordinal);

        if (expression is IdentifierNameSyntax identifier)
            return identifier.Identifier.Text.EndsWith("Array", StringComparison.Ordinal);

        return false;
    }

    private static bool LooksLikeUnmaterializedStream(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;

        bool streamLike = expr.Contains(".stream(", StringComparison.Ordinal)
            || expr.Contains("StreamSupport.stream(", StringComparison.Ordinal)
            || expr.Contains("Arrays.stream(", StringComparison.Ordinal)
            || expr.Contains("IntStream.range(", StringComparison.Ordinal)
            || expr.Contains(".sorted(", StringComparison.Ordinal)
            || expr.Contains(".map(", StringComparison.Ordinal)
            || expr.Contains(".filter(", StringComparison.Ordinal)
            || expr.Contains(".flatMap(", StringComparison.Ordinal);

        if (!streamLike)
            return false;

        return !expr.Contains(".collect(", StringComparison.Ordinal)
            && !expr.EndsWith(".toList()", StringComparison.Ordinal);
    }

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

    private string TransformObjectCreationWithInitializer(
        string typeName,
        ArgumentListSyntax? argumentList,
        InitializerExpressionSyntax initializer,
        ConversionContext context,
        INamedTypeSymbol? createdType)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Build constructor arguments (with narrowing-cast coercion via ArgumentTransformer)
        string ctorArgs = "";
        IMethodSymbol? ctorSymbol = null;
        if (argumentList != null && argumentList.Arguments.Count > 0)
        {
            if (context.SemanticModel != null && argumentList.Parent != null)
            {
                var symInfo = context.SemanticModel.GetSymbolInfo(argumentList.Parent);
                ctorSymbol = symInfo.Symbol as IMethodSymbol;
            }
            ctorArgs = ArgumentTransformer.TransformArgumentList(
                argumentList, context, facade, methodSymbol: ctorSymbol);
        }

        if (argumentList == null || argumentList.Arguments.Count == 0 || ctorSymbol == null)
        {
            AppendRuntimeClassArguments(createdType, context, ref ctorArgs);
        }

        // Emit the object creation and setter calls as pre-statements, then return the temp var.
        // This avoids the double-brace anonymous-subclass anti-pattern which leaks memory,
        // prevents the type from being final, and breaks equals() checks.
        string tmpVar = context.GenerateSyntheticName("_obj");
        var pendingAssignments = new List<string>();

        foreach (var expr in initializer.Expressions)
        {
            if (expr is AssignmentExpressionSyntax assignExpr)
            {
                var value = facade.Transform(assignExpr.Right, context);

                // Nested object-initializer assignments like "Settings = { NodeSeparation = ... }"
                // may currently lower to a TODO comment placeholder. Emitting a setter call with
                // that placeholder creates invalid Java (no effective argument). Skip for now.
                if (value.TrimStart().StartsWith("/* TODO: ObjectInitializerExpression", StringComparison.Ordinal))
                    continue;

                if (assignExpr.Left is IdentifierNameSyntax idName)
                {
                    // Use semantic model to distinguish public fields from properties:
                    // public fields → direct Java field assignment (e.g. _obj.Left = value)
                    // properties → setter method call (e.g. _obj.setLeft(value))
                    var memberSymbol = context.SemanticModel?.GetSymbolInfo(idName).Symbol;
                    if (memberSymbol is IFieldSymbol fieldSym)
                    {
                        value = ExpressionTransformerHelpers.AdaptExpressionToTargetType(assignExpr.Right, value, fieldSym.Type, context);
                        var javaFieldName = ConversionContext.EscapeJavaKeyword(fieldSym.Name);
                        pendingAssignments.Add($"{tmpVar}.{javaFieldName} = {value};");
                    }
                    else
                    {
                        var propertyType = memberSymbol is IPropertySymbol propertySymbol ? propertySymbol.Type : null;
                        value = ExpressionTransformerHelpers.AdaptExpressionToTargetType(assignExpr.Right, value, propertyType, context);
                        var propertyName = ConversionContext.EscapeJavaKeyword(idName.Identifier.Text);
                        var setterName = ConvertToSetter(propertyName);
                        pendingAssignments.Add($"{tmpVar}.{setterName}({value});");
                    }
                }
                else if (assignExpr.Left is MemberAccessExpressionSyntax memberAccess)
                {
                    var memberSymbol2 = context.SemanticModel?.GetSymbolInfo(memberAccess.Name).Symbol;
                    if (memberSymbol2 is IFieldSymbol fieldSym2)
                    {
                        value = ExpressionTransformerHelpers.AdaptExpressionToTargetType(assignExpr.Right, value, fieldSym2.Type, context);
                        var javaFieldName = ConversionContext.EscapeJavaKeyword(fieldSym2.Name);
                        pendingAssignments.Add($"{tmpVar}.{javaFieldName} = {value};");
                    }
                    else
                    {
                        var propertyType = memberSymbol2 is IPropertySymbol propertySymbol2 ? propertySymbol2.Type : null;
                        value = ExpressionTransformerHelpers.AdaptExpressionToTargetType(assignExpr.Right, value, propertyType, context);
                        var propertyName = ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text);
                        var setterName = ConvertToSetter(propertyName);
                        pendingAssignments.Add($"{tmpVar}.{setterName}({value});");
                    }
                }
                else
                {
                    var targetType = context.SemanticModel?.GetTypeInfo(assignExpr.Left).Type;
                    value = ExpressionTransformerHelpers.AdaptExpressionToTargetType(assignExpr.Right, value, targetType, context);
                    var target = facade.Transform(assignExpr.Left, context);
                    pendingAssignments.Add($"{tmpVar}.{target} = {value};");
                }
            }
        }

        context.AddPreStatement($"var {tmpVar} = new {typeName}({ctorArgs});");
        foreach (var assignment in pendingAssignments)
            context.AddPreStatement(assignment);

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

        // When targeting Java 25 with records enabled, synthesize a Java record instead of Map
        if (context.Options.UseRecords && context.Options.TargetJavaVersion >= JavaVersion.Java25)
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
        // erasure). Use the raw type (strip type arguments) in the new-expression and add an
        // unchecked cast to preserve generic type information for downstream consumers.
        string rawElementType = elementType;
        int genericArgStart = elementType.IndexOf('<');
        bool needsGenericArrayCast = genericArgStart > 0;
        if (needsGenericArrayCast)
            rawElementType = elementType.Substring(0, genericArgStart);

        // Fix: Java doesn't allow creating arrays of type parameters (e.g., new T[n]).
        // Use (T[]) new Object[n] cast pattern for all type parameter array creation.
        bool isTypeParameterArray = elemSemType != null && elemSemType.TypeKind == TypeKind.TypeParameter;
        string? constrainedArrayElementType = elemSemType is ITypeParameterSymbol typeParameterSymbol
            ? GetConstrainedArrayElementType(typeParameterSymbol, context)
            : null;

        // Single-dimension type parameter arrays → (T[]) new Object[n]
        if (isTypeParameterArray && sizes.Count == 1 && node.Initializer == null)
        {
            var sizeExpr = sizes[0];
            if (elemSemType is ITypeParameterSymbol typeParameterForRuntimeClass
                && context.TryGetRuntimeClassParameter(typeParameterForRuntimeClass.Name, out var runtimeClassParameter))
            {
                var lengthExpr = string.IsNullOrEmpty(sizeExpr) ? "0" : sizeExpr;
                return $"({elementType}[]) java.lang.reflect.Array.newInstance({runtimeClassParameter}, {lengthExpr})";
            }

            var runtimeType = string.IsNullOrEmpty(constrainedArrayElementType) ? "Object" : constrainedArrayElementType;
            if (string.IsNullOrEmpty(sizeExpr) || sizeExpr == "0")
            {
                return $"({elementType}[]) new {runtimeType}[0]";
            }
            else
            {
                return $"({elementType}[]) new {runtimeType}[{sizeExpr}]";
            }
        }

        var result = new StringBuilder();
        if (isTypeParameterArray)
        {
            // For 2D jagged type parameter arrays (e.g. new T[n][]),
            // use (T[][]) new Object[n][] with full rank brackets.
            if (sizes.Count == 2 && !string.IsNullOrEmpty(sizes[0]) && string.IsNullOrEmpty(sizes[1]))
            {
                var runtimeType = string.IsNullOrEmpty(constrainedArrayElementType) ? "Object" : constrainedArrayElementType;
                result.Append($"({elementType}[][]) new {runtimeType}[{sizes[0]}][]");
                return result.ToString();
            }

            // For type parameter arrays, use (T[][]...) new Object[...] with full rank.
            result.Append('(');
            result.Append(elementType);
            for (int i = 0; i < sizes.Count; i++)
                result.Append("[]");
            result.Append(") new ");
            result.Append(string.IsNullOrEmpty(constrainedArrayElementType) ? "Object" : constrainedArrayElementType);
        }
        else
        {
            if (needsGenericArrayCast)
            {
                result.Append('(');
                result.Append(elementType);
                for (int i = 0; i < sizes.Count; i++)
                    result.Append("[]");
                result.Append(") ");
            }
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

    private static string? GetConstrainedArrayElementType(ITypeParameterSymbol typeParameterSymbol, ConversionContext context)
    {
        var constraintType = typeParameterSymbol.ConstraintTypes
            .FirstOrDefault(t => t.SpecialType != SpecialType.System_Object);

        if (constraintType == null)
        {
            return null;
        }

        var mappedType = context.MapType(constraintType);
        if (string.IsNullOrWhiteSpace(mappedType))
        {
            return null;
        }

        var genericArgStart = mappedType.IndexOf('<');
        return genericArgStart > 0 ? mappedType[..genericArgStart] : mappedType;
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

        // If semantic inference failed (e.g. overloaded operators in large project mode),
        // try to infer from initializer expressions and operand symbols.
        if (string.IsNullOrWhiteSpace(elementType) && node.Initializer != null)
        {
            elementType = InferElementTypeFromInitializer(node.Initializer.Expressions, context);
        }

        if (string.IsNullOrWhiteSpace(elementType))
        {
            elementType = "Object";
        }

        // When the element type resolved to "Object" and all initializer elements are
        // anonymous-type creations in Java-records mode, synthesize the record and use its name.
        // This turns new Object[] { new A(1), ... } into new A[] { new A(1), ... } so that
        // stream lambdas can call typed accessors (e.g. x.getId()) without cast failures.
        if (elementType == "Object"
            && node.Initializer?.Expressions.Count > 0
            && node.Initializer.Expressions.All(e => e is AnonymousObjectCreationExpressionSyntax)
            && context.Options.UseRecords && context.Options.TargetJavaVersion >= JavaVersion.Java25)
        {
            var facade2 = ExpressionTransformerFacade.Instance;
            var firstAnon = (AnonymousObjectCreationExpressionSyntax)node.Initializer.Expressions[0];
            var (record, _) = AnonymousTypeRecordSynthesizer.SynthesizeForAnonymousType(
                firstAnon, context, expr => facade2.Transform(expr, context));
            elementType = record.RecordName;
        }

        // Java forbids generic array creation (e.g. new Pair<K,V>[] {...}).
        // For implicit arrays, use the raw component type in the new-expression
        // and add an unchecked cast to preserve generic type information for
        // downstream consumers (e.g. Arrays.stream() type inference).
        var rawElementType = elementType;
        var genericStart = rawElementType.IndexOf('<');
        bool needsGenericCast = genericStart > 0;
        if (needsGenericCast)
            rawElementType = rawElementType[..genericStart];

        var result = new StringBuilder();
        if (needsGenericCast)
        {
            result.Append('(');
            result.Append(elementType);
            for (int i = 0; i < node.Commas.Count + 1; i++)
                result.Append("[]");
            result.Append(") ");
        }

        result.Append("new ");
        result.Append(rawElementType);

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

    private static string InferElementTypeFromInitializer(SeparatedSyntaxList<ExpressionSyntax> expressions, ConversionContext context)
    {
        if (expressions.Count == 0)
        {
            return "Object";
        }

        var inferred = new List<string>();

        foreach (var expr in expressions)
        {
            var type = context.SemanticModel?.GetTypeInfo(expr).Type
                ?? context.SemanticModel?.GetTypeInfo(expr).ConvertedType;
            if (type != null)
            {
                var mapped = context.MapType(type);
                if (!string.IsNullOrWhiteSpace(mapped))
                {
                    inferred.Add(mapped);
                    continue;
                }
            }

            // Binary expressions may fail to resolve as a whole while operands still resolve.
            if (expr is BinaryExpressionSyntax binary)
            {
                var leftType = context.SemanticModel?.GetTypeInfo(binary.Left).Type;
                var rightType = context.SemanticModel?.GetTypeInfo(binary.Right).Type;
                if (leftType != null && rightType != null && SymbolEqualityComparer.Default.Equals(leftType, rightType))
                {
                    var mapped = context.MapType(leftType);
                    if (!string.IsNullOrWhiteSpace(mapped))
                    {
                        inferred.Add(mapped);
                    }
                }
            }
        }

        var first = inferred.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return "Object";
        }

        if (inferred.All(t => t == first))
        {
            return first;
        }

        return "Object";
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
        string typeName,
        InitializerExpressionSyntax initializer,
        ITypeSymbol? createdTypeSymbol,
        ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        INamedTypeSymbol? namedCreatedType = createdTypeSymbol as INamedTypeSymbol;
        ITypeSymbol? elementType = namedCreatedType?.TypeArguments.Length >= 1
            ? namedCreatedType.TypeArguments[0]
            : null;
        ITypeSymbol? keyType = namedCreatedType?.TypeArguments.Length >= 2
            ? namedCreatedType.TypeArguments[0]
            : null;
        ITypeSymbol? valueType = namedCreatedType?.TypeArguments.Length >= 2
            ? namedCreatedType.TypeArguments[1]
            : null;

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
                    key = ExpressionTransformerHelpers.AdaptExpressionToTargetType(complexInit.Expressions[0], key, keyType, context);
                    value = ExpressionTransformerHelpers.AdaptExpressionToTargetType(complexInit.Expressions[1], value, valueType, context);
                    context.AddPreStatement($"{tmpVar}.put({key}, {value});");
                }
            }
            return tmpVar;
        }

        // Set-like or List-like: emit as a single constructor expression
        var items = initializer.Expressions
            .Select(e => ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                e,
                facade.Transform(e, context),
                elementType,
                context))
            .ToList();
        var itemsStr = string.Join(", ", items);

        bool isSetLike = typeName.StartsWith("HashSet", StringComparison.Ordinal) ||
                         typeName.StartsWith("TreeSet", StringComparison.Ordinal) ||
                         typeName.StartsWith("LinkedHashSet", StringComparison.Ordinal);

        if (isSetLike)
        {
            if (context.Options.TargetJavaVersion >= JavaVersion.Java25)
            {
                context.AddImport("java.util.Set");
                return $"new {typeName}(Set.of({itemsStr}))";
            }
            context.AddImport("io.github.ningpp.compat.ArrayHelper");
            return $"new {typeName}(ArrayHelper.toList({itemsStr}))";
        }

        // Default: List-like
        context.AddImport("io.github.ningpp.compat.ArrayHelper");
        return $"new {typeName}(ArrayHelper.toList({itemsStr}))";
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
