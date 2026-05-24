using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Utilities;

internal static class RuntimeClassParameterHelper
{
    public static IReadOnlyList<ITypeParameterSymbol> GetRequiredTypeParameters(
        IMethodSymbol? methodSymbol,
        ConversionContext context)
    {
        if (methodSymbol == null || context.SemanticModel == null)
            return Array.Empty<ITypeParameterSymbol>();

        var visiting = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        return GetRequiredTypeParameters(methodSymbol.OriginalDefinition, context, visiting);
    }

    public static IReadOnlyList<string> GetRuntimeClassArguments(
        IMethodSymbol? methodSymbol,
        ConversionContext context)
    {
        if (methodSymbol == null)
            return Array.Empty<string>();

        var requiredTypeParameters = GetRequiredTypeParameters(methodSymbol, context);
        if (requiredTypeParameters.Count == 0)
            return Array.Empty<string>();

        var arguments = new List<string>();
        foreach (var typeParameter in requiredTypeParameters)
        {
            var runtimeType = ResolveRuntimeTypeArgument(typeParameter, methodSymbol);
            if (runtimeType != null)
                arguments.Add(ToClassArgument(runtimeType, context));
        }

        return arguments;
    }

    private static IReadOnlyList<ITypeParameterSymbol> GetRequiredTypeParameters(
        IMethodSymbol originalMethod,
        ConversionContext context,
        HashSet<IMethodSymbol> visiting)
    {
        if (context.TryGetCachedRuntimeClassRequiredTypeParameters(originalMethod, out var cached))
            return cached;

        if (!visiting.Add(originalMethod))
            return Array.Empty<ITypeParameterSymbol>();

        context.CacheRuntimeClassRequiredTypeParameters(originalMethod, Array.Empty<ITypeParameterSymbol>());

        try
        {
            var declaration = originalMethod.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();
            if (declaration == null)
                return Array.Empty<ITypeParameterSymbol>();

            var semanticModel = context.GetSemanticModelForTree(declaration.SyntaxTree) ?? context.SemanticModel;
            if (semanticModel == null)
                return Array.Empty<ITypeParameterSymbol>();

            var required = new List<ITypeParameterSymbol>();

            AddDirectArrayRequirements(declaration, semanticModel, originalMethod, required);
            AddForwardedInvocationRequirements(declaration, semanticModel, originalMethod, context, visiting, required);

            var result = required
                .OrderBy(tp => tp.Name, StringComparer.Ordinal)
                .ToList();
            context.CacheRuntimeClassRequiredTypeParameters(originalMethod, result);
            return result;
        }
        finally
        {
            visiting.Remove(originalMethod);
        }
    }

    private static void AddDirectArrayRequirements(
        MethodDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        List<ITypeParameterSymbol> required)
    {
        foreach (var arrayCreation in GetArrayCreations(declaration))
        {
            if (arrayCreation.Initializer != null)
                continue;

            var elementType = semanticModel.GetTypeInfo(arrayCreation.Type.ElementType).Type;
            if (elementType is ITypeParameterSymbol typeParameter
                && IsOwnedByMethodOrType(typeParameter, originalMethod))
            {
                AddUnique(required, typeParameter);
            }
        }
    }

    private static void AddForwardedInvocationRequirements(
        MethodDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        ConversionContext context,
        HashSet<IMethodSymbol> visiting,
        List<ITypeParameterSymbol> required)
    {
        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var invokedMethod = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (invokedMethod == null)
                continue;

            var invokedRequirements = GetRequiredTypeParameters(invokedMethod.OriginalDefinition, context, visiting);
            foreach (var invokedTypeParameter in invokedRequirements)
            {
                var runtimeType = ResolveRuntimeTypeArgument(invokedTypeParameter, invokedMethod);
                if (runtimeType is ITypeParameterSymbol runtimeTypeParameter
                    && IsOwnedByMethodOrType(runtimeTypeParameter, originalMethod))
                {
                    AddUnique(required, runtimeTypeParameter);
                }
            }
        }
    }

    private static IEnumerable<ArrayCreationExpressionSyntax> GetArrayCreations(MethodDeclarationSyntax declaration)
    {
        return declaration.Body != null
            ? declaration.Body.DescendantNodes().OfType<ArrayCreationExpressionSyntax>()
            : declaration.ExpressionBody?.Expression.DescendantNodesAndSelf().OfType<ArrayCreationExpressionSyntax>()
                ?? Enumerable.Empty<ArrayCreationExpressionSyntax>();
    }

    private static bool IsOwnedByMethodOrType(ITypeParameterSymbol typeParameter, IMethodSymbol originalMethod)
    {
        return SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringMethod, originalMethod)
            || SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringType, originalMethod.ContainingType);
    }

    private static void AddUnique(List<ITypeParameterSymbol> required, ITypeParameterSymbol typeParameter)
    {
        if (!required.Any(existing => SymbolEqualityComparer.Default.Equals(existing, typeParameter)))
            required.Add(typeParameter);
    }

    private static ITypeSymbol? ResolveRuntimeTypeArgument(
        ITypeParameterSymbol typeParameter,
        IMethodSymbol methodSymbol)
    {
        var originalMethod = methodSymbol.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringMethod, originalMethod))
        {
            var methodTypeParameters = originalMethod.TypeParameters;
            for (var i = 0; i < methodTypeParameters.Length && i < methodSymbol.TypeArguments.Length; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(methodTypeParameters[i], typeParameter))
                    return methodSymbol.TypeArguments[i];
            }
        }

        var containingType = methodSymbol.ContainingType;
        var originalType = containingType?.OriginalDefinition;
        if (containingType != null
            && originalType != null
            && SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringType, originalType))
        {
            var typeParameters = originalType.TypeParameters;
            for (var i = 0; i < typeParameters.Length && i < containingType.TypeArguments.Length; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(typeParameters[i], typeParameter))
                    return containingType.TypeArguments[i];
            }
        }

        return null;
    }

    private static string ToClassArgument(ITypeSymbol runtimeType, ConversionContext context)
    {
        if (runtimeType is ITypeParameterSymbol typeParameter
            && context.TryGetRuntimeClassParameter(typeParameter.Name, out var runtimeClassParameter))
        {
            return runtimeClassParameter;
        }

        var javaType = context.MapType(runtimeType);
        javaType = javaType switch
        {
            "int" => "Integer",
            "long" => "Long",
            "double" => "Double",
            "float" => "Float",
            "boolean" => "Boolean",
            "char" => "Character",
            "byte" => "Byte",
            "short" => "Short",
            _ => javaType
        };

        var genericStart = javaType.IndexOf('<');
        if (genericStart > 0)
            javaType = javaType[..genericStart];

        return $"{javaType}.class";
    }
}
