using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Utilities;

internal static class RuntimeClassParameterHelper
{
    public static IReadOnlyList<ITypeParameterSymbol> GetRequiredTypeParameters(
        INamedTypeSymbol? typeSymbol,
        ConversionContext context)
    {
        if (typeSymbol == null || context.SemanticModel == null)
            return Array.Empty<ITypeParameterSymbol>();

        var originalType = typeSymbol.OriginalDefinition;
        if (context.TryGetCachedRuntimeClassRequiredTypeParameters(originalType, out var cached))
            return cached;

        var required = new List<ITypeParameterSymbol>();
        var visiting = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        context.CacheRuntimeClassRequiredTypeParameters(originalType, Array.Empty<ITypeParameterSymbol>());

        foreach (var declaration in originalType.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<TypeDeclarationSyntax>())
        {
            var semanticModel = GetSemanticModelForDeclaration(declaration, context);
            if (semanticModel == null)
                continue;

            foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol)
                    AddClassOwnedRequirements(methodSymbol.OriginalDefinition, context, visiting, required);
            }

            foreach (var ctor in declaration.Members.OfType<ConstructorDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(ctor) is IMethodSymbol ctorSymbol)
                    AddClassOwnedRequirements(ctorSymbol.OriginalDefinition, context, visiting, required);
            }
        }

        AddBaseClassForwardingRequirements(originalType, context, required);

        var result = required
            .OrderBy(tp => tp.Name, StringComparer.Ordinal)
            .ToList();
        context.CacheRuntimeClassRequiredTypeParameters(originalType, result);
        if (result.Count > 0)
            context.Options.RuntimeClassParameters.RegisterTypeRequirements(originalType, result);
        return result;
    }

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

        if (methodSymbol.MethodKind == MethodKind.Constructor)
        {
            return GetRuntimeClassArguments(methodSymbol.ContainingType, context);
        }

        var originalMethod = methodSymbol.OriginalDefinition;
        var requiredTypeParameters = GetRequiredTypeParameters(methodSymbol, context)
            .Where(tp => SymbolEqualityComparer.Default.Equals(tp.DeclaringMethod, originalMethod))
            .ToList();
        if (requiredTypeParameters.Count == 0)
            return GetRegisteredRuntimeClassArguments(methodSymbol, context);

        var arguments = new List<string>();
        foreach (var typeParameter in requiredTypeParameters)
        {
            var runtimeType = ResolveRuntimeTypeArgument(typeParameter, methodSymbol);
            if (runtimeType != null)
                arguments.Add(ToClassArgument(runtimeType, context, preferPrimitiveClassLiteral: true));
        }

        if (arguments.Count > 0)
            return arguments;

        return GetRegisteredRuntimeClassArguments(methodSymbol, context);
    }

    public static IReadOnlyList<string> GetRuntimeClassArguments(
        INamedTypeSymbol? typeSymbol,
        ConversionContext context)
    {
        if (typeSymbol == null)
            return Array.Empty<string>();

        var requiredTypeParameters = GetRequiredTypeParameters(typeSymbol, context);
        if (requiredTypeParameters.Count == 0)
            return GetRegisteredRuntimeClassArguments(typeSymbol, context);

        var arguments = new List<string>();
        foreach (var typeParameter in requiredTypeParameters)
        {
            var runtimeType = ResolveRuntimeTypeArgument(typeParameter, typeSymbol);
            if (runtimeType != null)
                arguments.Add(ToClassArgument(runtimeType, context, preferPrimitiveClassLiteral: false));
        }

        if (arguments.Count > 0)
            return arguments;

        return GetRegisteredRuntimeClassArguments(typeSymbol, context);
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
                .FirstOrDefault();
            if (declaration is not MethodDeclarationSyntax and not ConstructorDeclarationSyntax)
                return Array.Empty<ITypeParameterSymbol>();

            var semanticModel = GetSemanticModelForDeclaration(declaration, context);
            if (semanticModel == null)
                return Array.Empty<ITypeParameterSymbol>();

            var required = new List<ITypeParameterSymbol>();

            switch (declaration)
            {
                case MethodDeclarationSyntax methodDeclaration:
                    AddDirectArrayRequirements(methodDeclaration, semanticModel, originalMethod, required);
                    AddFrameworkToArrayRequirements(methodDeclaration, semanticModel, originalMethod, required);
                    AddForwardedInvocationRequirements(methodDeclaration, semanticModel, originalMethod, context, visiting, required);
                    AddObjectCreationRequirements(methodDeclaration, semanticModel, originalMethod, context, required);
                    break;
                case ConstructorDeclarationSyntax constructorDeclaration:
                    AddDirectArrayRequirements(constructorDeclaration, semanticModel, originalMethod, required);
                    AddFrameworkToArrayRequirements(constructorDeclaration, semanticModel, originalMethod, required);
                    AddForwardedInvocationRequirements(constructorDeclaration, semanticModel, originalMethod, context, visiting, required);
                    AddObjectCreationRequirements(constructorDeclaration, semanticModel, originalMethod, context, required);
                    break;
            }

            var result = required
                .OrderBy(tp => tp.Name, StringComparer.Ordinal)
                .ToList();
            context.CacheRuntimeClassRequiredTypeParameters(originalMethod, result);
            if (result.Count > 0)
                context.Options.RuntimeClassParameters.RegisterMethodRequirements(originalMethod, result);
            return result;
        }
        finally
        {
            visiting.Remove(originalMethod);
        }
    }

    private static void AddClassOwnedRequirements(
        IMethodSymbol originalMethod,
        ConversionContext context,
        HashSet<IMethodSymbol> visiting,
        List<ITypeParameterSymbol> required)
    {
        foreach (var typeParameter in GetRequiredTypeParameters(originalMethod, context, visiting))
        {
            if (typeParameter.DeclaringMethod == null)
                AddUnique(required, typeParameter);
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

            if (IsZeroLengthArrayInIterableContext(arrayCreation, semanticModel))
                continue;

            var elementType = semanticModel.GetTypeInfo(arrayCreation.Type.ElementType).Type;
            if (elementType is ITypeParameterSymbol typeParameter
                && IsOwnedByMethodOrType(typeParameter, originalMethod))
            {
                AddUnique(required, typeParameter);
            }
        }
    }

    private static void AddFrameworkToArrayRequirements(
        MethodDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        List<ITypeParameterSymbol> required)
    {
        AddFrameworkToArrayRequirements((SyntaxNode)declaration, semanticModel, originalMethod, required);
    }

    private static void AddFrameworkToArrayRequirements(
        ConstructorDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        List<ITypeParameterSymbol> required)
    {
        AddFrameworkToArrayRequirements((SyntaxNode)declaration, semanticModel, originalMethod, required);
    }

    private static void AddFrameworkToArrayRequirements(
        SyntaxNode declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        List<ITypeParameterSymbol> required)
    {
        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsFrameworkToArrayInvocation(invocation, semanticModel))
                continue;

            var typeInfo = semanticModel.GetTypeInfo(invocation);
            var arrayType = typeInfo.Type as IArrayTypeSymbol
                ?? typeInfo.ConvertedType as IArrayTypeSymbol;
            if (arrayType?.ElementType is ITypeParameterSymbol typeParameter
                && IsOwnedByMethodOrType(typeParameter, originalMethod))
            {
                AddUnique(required, typeParameter);
            }
        }
    }

    private static bool IsFrameworkToArrayInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
            return false;

        if (methodSymbol.Name != "ToArray")
            return false;

        if (methodSymbol.ReturnType is not IArrayTypeSymbol)
            return false;

        var containingType = methodSymbol.ContainingType?.OriginalDefinition.ToDisplayString();
        return containingType is "System.Linq.Enumerable"
            or "System.Collections.Generic.List<T>";
    }

    private static void AddBaseClassForwardingRequirements(
        INamedTypeSymbol originalType,
        ConversionContext context,
        List<ITypeParameterSymbol> required)
    {
        var baseType = originalType.BaseType;
        if (baseType == null || baseType.SpecialType == SpecialType.System_Object)
            return;

        foreach (var baseRequirement in GetRequiredTypeParameters(baseType, context))
        {
            var runtimeType = ResolveRuntimeTypeArgument(baseRequirement, baseType);
            if (runtimeType is ITypeParameterSymbol runtimeTypeParameter
                && SymbolEqualityComparer.Default.Equals(runtimeTypeParameter.DeclaringType, originalType))
            {
                AddUnique(required, runtimeTypeParameter);
            }
        }
    }

    private static IReadOnlyList<string> GetRegisteredRuntimeClassArguments(
        IMethodSymbol methodSymbol,
        ConversionContext context)
    {
        if (!context.Options.RuntimeClassParameters.TryGetMethodRequirements(methodSymbol, out var requirements)
            || requirements.Count == 0)
        {
            return Array.Empty<string>();
        }

        var arguments = new List<string>();
        foreach (var requirement in requirements)
        {
            if (requirement.Kind != RuntimeClassTypeParameterRequirementKind.Method)
                continue;

            var runtimeType = ResolveRegisteredRuntimeTypeArgument(requirement, methodSymbol);
            if (runtimeType != null)
                arguments.Add(ToClassArgument(runtimeType, context, preferPrimitiveClassLiteral: true));
        }

        return arguments;
    }

    private static IReadOnlyList<string> GetRegisteredRuntimeClassArguments(
        INamedTypeSymbol typeSymbol,
        ConversionContext context)
    {
        if (!context.Options.RuntimeClassParameters.TryGetTypeRequirements(typeSymbol, out var requirements)
            || requirements.Count == 0)
        {
            return Array.Empty<string>();
        }

        var arguments = new List<string>();
        foreach (var requirement in requirements)
        {
            var runtimeType = ResolveRegisteredRuntimeTypeArgument(requirement, typeSymbol);
            if (runtimeType != null)
                arguments.Add(ToClassArgument(runtimeType, context, preferPrimitiveClassLiteral: false));
        }

        return arguments;
    }

    private static void AddDirectArrayRequirements(
        ConstructorDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        List<ITypeParameterSymbol> required)
    {
        foreach (var arrayCreation in GetArrayCreations(declaration))
        {
            if (arrayCreation.Initializer != null)
                continue;

            if (IsZeroLengthArrayInIterableContext(arrayCreation, semanticModel))
                continue;

            var elementType = semanticModel.GetTypeInfo(arrayCreation.Type.ElementType).Type;
            if (elementType is ITypeParameterSymbol typeParameter
                && IsOwnedByMethodOrType(typeParameter, originalMethod))
            {
                AddUnique(required, typeParameter);
            }
        }
    }

    private static void AddObjectCreationRequirements(
        MethodDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        ConversionContext context,
        List<ITypeParameterSymbol> required)
    {
        AddObjectCreationRequirements((SyntaxNode)declaration, semanticModel, originalMethod, context, required);
    }

    private static void AddObjectCreationRequirements(
        ConstructorDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        ConversionContext context,
        List<ITypeParameterSymbol> required)
    {
        AddObjectCreationRequirements((SyntaxNode)declaration, semanticModel, originalMethod, context, required);
    }

    private static void AddObjectCreationRequirements(
        SyntaxNode declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        ConversionContext context,
        List<ITypeParameterSymbol> required)
    {
        foreach (var creation in declaration.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            var createdType = semanticModel.GetTypeInfo(creation).Type as INamedTypeSymbol;
            if (createdType == null)
                continue;

            foreach (var createdRequirement in GetRequiredTypeParameters(createdType, context))
            {
                var runtimeType = ResolveRuntimeTypeArgument(createdRequirement, createdType);
                if (runtimeType is ITypeParameterSymbol runtimeTypeParameter
                    && IsOwnedByMethodOrType(runtimeTypeParameter, originalMethod))
                {
                    AddUnique(required, runtimeTypeParameter);
                }
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
            AddForwardedRequirements(invokedMethod, originalMethod, context, visiting, required);
        }
    }

    private static void AddForwardedInvocationRequirements(
        ConstructorDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        ConversionContext context,
        HashSet<IMethodSymbol> visiting,
        List<ITypeParameterSymbol> required)
    {
        if (declaration.Initializer != null)
        {
            var invokedConstructor = semanticModel.GetSymbolInfo(declaration.Initializer).Symbol as IMethodSymbol;
            AddForwardedRequirements(invokedConstructor, originalMethod, context, visiting, required);
        }

        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var invokedMethod = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            AddForwardedRequirements(invokedMethod, originalMethod, context, visiting, required);
        }
    }

    private static void AddForwardedRequirements(
        IMethodSymbol? invokedMethod,
        IMethodSymbol originalMethod,
        ConversionContext context,
        HashSet<IMethodSymbol> visiting,
        List<ITypeParameterSymbol> required)
    {
        if (invokedMethod == null)
            return;

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

    private static IEnumerable<ArrayCreationExpressionSyntax> GetArrayCreations(MethodDeclarationSyntax declaration)
    {
        return declaration.Body != null
            ? declaration.Body.DescendantNodes().OfType<ArrayCreationExpressionSyntax>()
            : declaration.ExpressionBody?.Expression.DescendantNodesAndSelf().OfType<ArrayCreationExpressionSyntax>()
                ?? Enumerable.Empty<ArrayCreationExpressionSyntax>();
    }

    private static IEnumerable<ArrayCreationExpressionSyntax> GetArrayCreations(ConstructorDeclarationSyntax declaration)
    {
        return declaration.Body != null
            ? declaration.Body.DescendantNodes().OfType<ArrayCreationExpressionSyntax>()
            : declaration.ExpressionBody?.Expression.DescendantNodesAndSelf().OfType<ArrayCreationExpressionSyntax>()
                ?? Enumerable.Empty<ArrayCreationExpressionSyntax>();
    }

    private static SemanticModel? GetSemanticModelForDeclaration(SyntaxNode declaration, ConversionContext context)
    {
        var semanticModel = context.GetSemanticModelForTree(declaration.SyntaxTree);
        if (semanticModel?.SyntaxTree == declaration.SyntaxTree)
            return semanticModel;

        return null;
    }

    private static bool IsOwnedByMethodOrType(ITypeParameterSymbol typeParameter, IMethodSymbol originalMethod)
    {
        return SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringMethod, originalMethod)
            || SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringType, originalMethod.ContainingType);
    }

    private static bool IsZeroLengthArrayInIterableContext(
        ArrayCreationExpressionSyntax arrayCreation,
        SemanticModel semanticModel)
    {
        var rank = arrayCreation.Type.RankSpecifiers.FirstOrDefault();
        if (rank == null || rank.Sizes.Count != 1)
            return false;

        var size = rank.Sizes[0];
        if (!IsConstantZero(size, semanticModel))
            return false;

        for (SyntaxNode? current = arrayCreation; current != null; current = current.Parent)
        {
            if (current is MethodDeclarationSyntax or ConstructorDeclarationSyntax)
                break;

            if (current is ExpressionSyntax expression)
            {
                var typeInfo = semanticModel.GetTypeInfo(expression);
                if (IsIterableLike(typeInfo.ConvertedType))
                    return true;
            }

            if (current is ReturnStatementSyntax returnStatement)
            {
                var method = returnStatement.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (method != null)
                {
                    var returnType = semanticModel.GetTypeInfo(method.ReturnType).Type;
                    if (IsIterableLike(returnType))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool IsConstantZero(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        if (expression is LiteralExpressionSyntax literal
            && literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression)
            && literal.Token.Value is int intValue)
        {
            return intValue == 0;
        }

        var constant = semanticModel.GetConstantValue(expression);
        return constant.HasValue
            && constant.Value is int constantInt
            && constantInt == 0;
    }

    private static bool IsIterableLike(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        var original = named.OriginalDefinition.ToDisplayString();
        return original is "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.IEnumerable"
            or "System.Collections.Generic.ICollection<T>"
            or "System.Collections.ICollection"
            or "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.IReadOnlyCollection<T>"
            or "System.Collections.Generic.IReadOnlyList<T>";
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

    private static ITypeSymbol? ResolveRegisteredRuntimeTypeArgument(
        RuntimeClassTypeParameterRequirement requirement,
        IMethodSymbol methodSymbol)
    {
        if (requirement.Kind == RuntimeClassTypeParameterRequirementKind.Method
            && requirement.Ordinal >= 0
            && requirement.Ordinal < methodSymbol.TypeArguments.Length)
        {
            return methodSymbol.TypeArguments[requirement.Ordinal];
        }

        if (requirement.Kind == RuntimeClassTypeParameterRequirementKind.Type)
        {
            return ResolveRegisteredRuntimeTypeArgument(requirement, methodSymbol.ContainingType);
        }

        return null;
    }

    private static ITypeSymbol? ResolveRuntimeTypeArgument(
        ITypeParameterSymbol typeParameter,
        INamedTypeSymbol typeSymbol)
    {
        var originalType = typeSymbol.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringType, originalType))
        {
            var typeParameters = originalType.TypeParameters;
            for (var i = 0; i < typeParameters.Length && i < typeSymbol.TypeArguments.Length; i++)
            {
                if (SymbolEqualityComparer.Default.Equals(typeParameters[i], typeParameter))
                    return typeSymbol.TypeArguments[i];
            }
        }

        return null;
    }

    private static ITypeSymbol? ResolveRegisteredRuntimeTypeArgument(
        RuntimeClassTypeParameterRequirement requirement,
        INamedTypeSymbol typeSymbol)
    {
        if (requirement.Kind != RuntimeClassTypeParameterRequirementKind.Type
            || requirement.Ordinal < 0
            || requirement.Ordinal >= typeSymbol.TypeArguments.Length)
        {
            return null;
        }

        return typeSymbol.TypeArguments[requirement.Ordinal];
    }

    private static string ToClassArgument(
        ITypeSymbol runtimeType,
        ConversionContext context,
        bool preferPrimitiveClassLiteral)
    {
        if (runtimeType is ITypeParameterSymbol typeParameter
            && context.TryGetRuntimeClassParameter(typeParameter.Name, out var runtimeClassParameter))
        {
            return runtimeClassParameter;
        }

        if (preferPrimitiveClassLiteral)
        {
            var mappedType = context.MapType(runtimeType);
            if (ExpressionTransformerHelpers.IsJavaPrimitiveType(mappedType))
            {
                return $"{mappedType}.class";
            }

            var stripped = ExpressionTransformerHelpers.StripTypeArguments(mappedType);
            return $"{stripped}.class";
        }

        var javaType = context.MapType(runtimeType);
        javaType = ExpressionTransformerHelpers.ToRuntimeTypeForClassLiteral(javaType);

        return $"{javaType}.class";
    }
}
