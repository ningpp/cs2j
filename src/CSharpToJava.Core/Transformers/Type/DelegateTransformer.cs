using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// Delegate transformer - converts C# delegate declarations to Java @FunctionalInterface interfaces.
/// Example:  delegate void ShowGraph(GeometryGraph g);
/// Becomes:  @FunctionalInterface public interface ShowGraph { void invoke(GeometryGraph g); }
/// </summary>
public class DelegateTransformer : IDelegateTransformer
{
    public JavaTypeDeclaration? TransformDelegate(DelegateDeclarationSyntax node, ConversionContext context)
    {
        var javaInterface = new JavaInterfaceDeclaration
        {
            Name = node.Identifier.Text,
            Modifiers = ConvertModifiers(node.Modifiers),
        };

        // Mark as @FunctionalInterface
        javaInterface.Annotations.Add(new JavaAnnotation("FunctionalInterface"));

        // Build the single abstract method first (needed for type parameter filtering).
        var returnType = GetReturnType(node, context);
        var paramCount = node.ParameterList?.Parameters.Count ?? 0;
        bool isVoid = node.ReturnType is PredefinedTypeSyntax predefined2 &&
                      predefined2.Keyword.IsKind(SyntaxKind.VoidKeyword);

        // SAM method name must match InferSamMethodName in InvocationExpressionTransformer
        // so that declarations and call sites agree on the method name.
        var samMethodName = InferSamMethodName(isVoid, paramCount);

        var invokeMethod = new JavaMethodDeclaration
        {
            Name = samMethodName,
            Modifiers = JavaModifiers.None, // interface methods are implicitly public abstract
            ReturnType = returnType,
        };

        foreach (var param in node.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var (javaType, isVarArgs) = ResolveParamType(param, context);
            var paramName = ConversionContext.EscapeJavaKeyword(param.Identifier.Text);
            invokeMethod.Parameters.Add(new JavaParameter(javaType, paramName) { IsVarArgs = isVarArgs });
        }

        // Collect type names referenced by the delegate signature (return type + parameters)
        // to filter out enclosing type parameters that are not actually used.
        var referencedTypeNames = CollectReferencedTypeNames(node, context);

        // Handle type parameters (generic delegates)
        var allTypeParams = new List<string>();

        // 1. Enclosing class type parameters — only include those actually referenced
        //    by the delegate's return type or parameter types.
        var sym = context.SemanticModel?.GetDeclaredSymbol(node) as INamedTypeSymbol;
        if (sym != null)
        {
            var enclosingParams = new List<JavaTypeParameter>();
            var cur = sym.ContainingType;
            while (cur != null)
            {
                enclosingParams.InsertRange(0, cur.TypeParameters
                    .Where(tp => referencedTypeNames.Contains(tp.Name)
                                 && !allTypeParams.Contains(tp.Name)
                                 && enclosingParams.All(ep => ep.Name != tp.Name))
                    .Select(tp => new JavaTypeParameter(tp.Name)));
                cur = cur.ContainingType;
            }
            foreach (var ep in enclosingParams)
                allTypeParams.Add(ep.Name);
            javaInterface.TypeParameters.InsertRange(0, enclosingParams);
        }

        // 2. Delegate's own type parameters
        foreach (var typeParam in node.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            if (!allTypeParams.Contains(typeParam.Identifier.Text))
            {
                var jtp = new JavaTypeParameter(typeParam.Identifier.Text);
                javaInterface.TypeParameters.Add(jtp);
                allTypeParams.Add(typeParam.Identifier.Text);
            }

            if (typeParam.VarianceKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                context.Diagnostics.Warning(
                    $"Covariant type parameter 'out {typeParam.Identifier.Text}' — Java uses use-site variance; declaration-site variance dropped",
                    typeParam.GetLocation(),
                    code: "CS2J1003",
                    category: "GenericVariance");
            }
            else if (typeParam.VarianceKeyword.IsKind(SyntaxKind.InKeyword))
            {
                context.Diagnostics.Warning(
                    $"Contravariant type parameter 'in {typeParam.Identifier.Text}' — Java uses use-site variance; declaration-site variance dropped",
                    typeParam.GetLocation(),
                    code: "CS2J1003",
                    category: "GenericVariance");
            }
        }

        // Propagate type parameter constraints
        ApplyTypeParameterConstraints(node, javaInterface, context);

        javaInterface.Methods.Add(invokeMethod);
        javaInterface.Methods.Add(new JavaMethodDeclaration
        {
            Name = "getTarget",
            Modifiers = JavaModifiers.Default,
            ReturnType = "Object",
            Body = "return null;"
        });

        return javaInterface;
    }

    /// <summary>
    /// Infer the Java SAM method name from delegate signature shape.
    /// This MUST stay in sync with InferSamMethodName in InvocationExpressionTransformer.
    /// </summary>
    internal static string InferSamMethodName(bool returnsVoid, int parameterCount) => (returnsVoid, parameterCount) switch
    {
        (true, 0) => "run",       // Runnable
        (true, _) => "accept",    // Consumer/BiConsumer
        (false, 0) => "get",      // Supplier
        _ => "apply",             // Function/BiFunction
    };

    /// <summary>
    /// Collect all type-parameter-like names that appear in the delegate's return type
    /// and parameter types. Used to filter out enclosing type parameters that are not
    /// actually referenced.
    /// </summary>
    private static HashSet<string> CollectReferencedTypeNames(DelegateDeclarationSyntax node, ConversionContext context)
    {
        var names = new HashSet<string>();
        CollectTypeNames(node.ReturnType, names);
        foreach (var param in node.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            if (param.Type != null)
                CollectTypeNames(param.Type, names);
        }
        return names;
    }

    private static void CollectTypeNames(TypeSyntax type, HashSet<string> names)
    {
        switch (type)
        {
            case IdentifierNameSyntax id:
                names.Add(id.Identifier.Text);
                break;
            case GenericNameSyntax generic:
                names.Add(generic.Identifier.Text);
                foreach (var arg in generic.TypeArgumentList.Arguments)
                    CollectTypeNames(arg, names);
                break;
            case ArrayTypeSyntax array:
                CollectTypeNames(array.ElementType, names);
                break;
            case NullableTypeSyntax nullable:
                CollectTypeNames(nullable.ElementType, names);
                break;
            case QualifiedNameSyntax qualified:
                CollectTypeNames(qualified.Right, names);
                break;
            case TupleTypeSyntax tuple:
                foreach (var element in tuple.Elements)
                    CollectTypeNames(element.Type, names);
                break;
        }
    }

    private (string javaType, bool isVarArgs) ResolveParamType(ParameterSyntax param, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(param.Type!);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null
            ? context.MapType(typeInfo.Value.Type)
            : "Object";

        foreach (var mod in param.Modifiers)
        {
            if (mod.IsKind(SyntaxKind.RefKeyword) || mod.IsKind(SyntaxKind.OutKeyword))
                return (HolderTypeResolver.GetHolderType(javaType), false);
            if (mod.IsKind(SyntaxKind.InKeyword))
                return (javaType, false); // in == read-only ref; in Java just pass by value
            if (mod.IsKind(SyntaxKind.ParamsKeyword))
                return (javaType, true);
        }

        return (javaType, false);
    }

    private string GetReturnType(DelegateDeclarationSyntax node, ConversionContext context)
    {
        if (node.ReturnType is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return "void";
        }
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.ReturnType);
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            return context.MapType(typeInfo.Value.Type);
        }
        return "Object";
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;
        foreach (var mod in modifiers)
        {
            result |= mod.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                _ => JavaModifiers.None
            };
        }
        return result;
    }

    private void ApplyTypeParameterConstraints(DelegateDeclarationSyntax node, JavaInterfaceDeclaration javaInterface, ConversionContext context)
    {
        if (node.ConstraintClauses.Count == 0) return;
        ClassTransformer.ApplyTypeParameterConstraints(node.ConstraintClauses, javaInterface.TypeParameters, context);
    }

    /// <summary>
    /// Converts a Java type to its corresponding holder class name for ref/out parameters.
    /// Delegates to <see cref="HolderTypeResolver.GetHolderType"/> — kept for backward compatibility.
    /// </summary>
    [System.Obsolete("Use HolderTypeResolver.GetHolderType() directly.")]
    internal static string GetHolderType(string javaType)
        => HolderTypeResolver.GetHolderType(javaType);
}
