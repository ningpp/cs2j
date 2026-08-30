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

            foreach (var prop in declaration.Members.OfType<PropertyDeclarationSyntax>())
            {
                // Property getters (e.g. `Type TypeToConvert => typeof(T)`) also consume
                // class-level type parameters via typeof(T) — scan them like methods.
                var propSymbol = semanticModel.GetDeclaredSymbol(prop);
                if (propSymbol is IPropertySymbol propSymbol2
                    && propSymbol2.GetMethod != null)
                    AddClassOwnedRequirements(propSymbol2.GetMethod.OriginalDefinition, context, visiting, required);
            }

            foreach (var ctor in declaration.Members.OfType<ConstructorDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(ctor) is IMethodSymbol ctorSymbol)
                    AddClassOwnedRequirements(ctorSymbol.OriginalDefinition, context, visiting, required);
            }

            // Field initializers run inside the constructor in C#; their typeof(T)
            // references class-level type parameters that need runtime Class fields.
            foreach (var field in declaration.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var typeOf in field.DescendantNodes().OfType<TypeOfExpressionSyntax>())
                {
                    if (semanticModel.GetTypeInfo(typeOf.Type).Type is ITypeParameterSymbol tp
                        && tp.DeclaringMethod == null)
                        AddUnique(required, tp);
                }
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
    {        if (typeSymbol == null)
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
        if (context.TryGetCachedRuntimeClassRequiredTypeParameters(originalMethod, out var cached)
            && originalMethod.MethodKind != MethodKind.PropertyGet)
            return cached;

        if (!visiting.Add(originalMethod))
            return Array.Empty<ITypeParameterSymbol>();

        context.CacheRuntimeClassRequiredTypeParameters(originalMethod, Array.Empty<ITypeParameterSymbol>());

        try
        {
            var declaration = originalMethod.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .FirstOrDefault();
            if (originalMethod.MethodKind == MethodKind.PropertyGet)
            {
                // Getter expression bodies (e.g. `Type TypeToConvert => typeof(T)`) consume
                // class-level type parameters via typeof(T); nothing else needs scanning.
                // Expression-bodied properties give the getter no standalone syntax
                // reference, so locate the property declaration via the associated symbol.
                var propDeclaration = declaration as PropertyDeclarationSyntax;
                if (propDeclaration == null
                    && originalMethod.AssociatedSymbol is IPropertySymbol associatedProp)
                {
                    propDeclaration = associatedProp.DeclaringSyntaxReferences
                        .Select(r => r.GetSyntax())
                        .OfType<PropertyDeclarationSyntax>()
                        .FirstOrDefault();
                }
                var getterRequired = new List<ITypeParameterSymbol>();
                var propModel = propDeclaration != null
                    ? GetSemanticModelForDeclaration(propDeclaration, context)
                    : null;
                if (propModel != null)
                {
                    foreach (var typeOf in propDeclaration!.DescendantNodes().OfType<TypeOfExpressionSyntax>())
                    {
                        if (propModel.GetTypeInfo(typeOf.Type).Type is ITypeParameterSymbol tp
                            && tp.DeclaringMethod == null)
                            AddUnique(getterRequired, tp);
                    }
                }
                context.CacheRuntimeClassRequiredTypeParameters(originalMethod, getterRequired);
                if (getterRequired.Count > 0)
                    context.Options.RuntimeClassParameters.RegisterMethodRequirements(originalMethod, getterRequired);
                return getterRequired;
            }
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
                    AddTypeOfDefaultRequirements(methodDeclaration, semanticModel, originalMethod, required);
                    AddArrayCreateInstanceRequirements(methodDeclaration, semanticModel, originalMethod, required);
                    AddJsonDeserializeRequirements(methodDeclaration, semanticModel, originalMethod, context, required);
                    break;
                case ConstructorDeclarationSyntax constructorDeclaration:
                    AddDirectArrayRequirements(constructorDeclaration, semanticModel, originalMethod, required);
                    AddFrameworkToArrayRequirements(constructorDeclaration, semanticModel, originalMethod, required);
                    AddForwardedInvocationRequirements(constructorDeclaration, semanticModel, originalMethod, context, visiting, required);
                    AddObjectCreationRequirements(constructorDeclaration, semanticModel, originalMethod, context, required);
                    AddTypeOfDefaultRequirements(constructorDeclaration, semanticModel, originalMethod, required);
                    AddArrayCreateInstanceRequirements(constructorDeclaration, semanticModel, originalMethod, required);
                    AddJsonDeserializeRequirements(constructorDeclaration, semanticModel, originalMethod, context, required);
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

    /// <summary>
    /// Scans for Array.CreateInstance(typeof(T), n) calls that were produced by
    /// GenericPreprocessor rewriting `new T[n]` where T is a method-level type
    /// parameter. The rewritten form is an InvocationExpressionSyntax, so
    /// AddDirectArrayRequirements (which scans ArrayCreationExpressionSyntax)
    /// misses it. This ensures the method-level type parameter is registered
    /// as a runtime class requirement, which in turn causes
    /// ShouldReturnObjectForRuntimeTypeParameterArray to change the return
    /// type from T[] to Object (avoiding ClassCastException for primitive arrays).
    /// </summary>
    private static void AddArrayCreateInstanceRequirements(
        SyntaxNode declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        List<ITypeParameterSymbol> required)
    {
        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name.Identifier.ValueText != "CreateInstance")
                continue;

            // Check that the receiver is System.Array or Array
            var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol == null)
            {
                var receiverText = memberAccess.Expression.ToString();
                if (receiverText != "System.Array" && receiverText != "Array")
                    continue;
            }
            else if (methodSymbol.ContainingType?.ToDisplayString() != "System.Array")
            {
                continue;
            }

            // Array.CreateInstance(typeof(T), n) — first argument is typeof(T)
            if (invocation.ArgumentList.Arguments.Count < 1)
                continue;

            var firstArg = invocation.ArgumentList.Arguments[0].Expression;
            if (firstArg is not TypeOfExpressionSyntax typeOfExpr)
                continue;

            if (semanticModel.GetTypeInfo(typeOfExpr.Type).Type is ITypeParameterSymbol typeParameter
                && typeParameter.DeclaringMethod != null
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

    /// <summary>
    /// Scans for JsonSerializer.Deserialize&lt;T&gt; where T is a METHOD-level type
    /// parameter. Such bodies need a TypeReference&lt;T&gt; token (not a Class&lt;T&gt;
    /// token) so call sites can pass constructed generic types (e.g.
    /// List&lt;GeometryCase&gt;) without losing the element type to erasure.
    /// </summary>
    private static void AddJsonDeserializeRequirements(
        SyntaxNode declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        ConversionContext context,
        List<ITypeParameterSymbol> required)
    {
        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
                continue;

            if (methodSymbol.Name != "Deserialize" || !methodSymbol.IsGenericMethod)
                continue;
            if (methodSymbol.ContainingType.ToDisplayString() != "System.Text.Json.JsonSerializer")
                continue;
            if (methodSymbol.TypeArguments.Length != 1)
                continue;
            if (methodSymbol.TypeArguments[0] is not ITypeParameterSymbol typeParam)
                continue;
            if (!SymbolEqualityComparer.Default.Equals(typeParam.DeclaringMethod, originalMethod))
                continue;
            // Marker only: the type parameter must NOT join the trailing Class<?>
            // clazz-parameter list (AddRuntimeClassParametersForTypeParameterArrays).
            // The definition site registers the pending requirement itself and the
            // signature is patched with a leading TypeReference<T> parameter.
            context.Options.RuntimeClassParameters.RegisterMethodTypeReference(originalMethod, typeParam.Name);
        }
    }

    private static void AddTypeOfDefaultRequirements(
        MethodDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        List<ITypeParameterSymbol> required)
    {
        AddTypeOfDefaultRequirements((SyntaxNode)declaration, semanticModel, originalMethod, required);
    }

    private static void AddTypeOfDefaultRequirements(
        ConstructorDeclarationSyntax declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        List<ITypeParameterSymbol> required)
    {
        AddTypeOfDefaultRequirements((SyntaxNode)declaration, semanticModel, originalMethod, required);
    }

    /// <summary>
    /// Scans for typeof(T) and default(T) / default literal where T is a class-level
    /// type parameter. These usages require a runtime Class field on the declaring
    /// type so that instance methods can reference it without adding a method-level
    /// Class&lt;T&gt; parameter.
    ///
    /// <para>The bare <c>default</c> literal (C# 7.1+, produced by GenericPreprocessor
    /// rewriting <c>default(T)</c>) is also scanned. For the literal form, the target
    /// type is inferred from the semantic model's <c>ConvertedType</c> (the type the
    /// expression is being assigned/returned as). Without this, GenericPreprocessor
    /// rewrites <c>default(T)</c> → <c>default</c> before the requirements scan runs,
    /// causing the class-level type parameter to be missed and <c>DefaultValue.of()</c>
    /// (which returns null) to be emitted instead of <c>DefaultValue.ofClass(field)</c>
    /// (which returns the correct zero value for primitives like int → 0).</para>
    /// </summary>
    private static void AddTypeOfDefaultRequirements(
        SyntaxNode declaration,
        SemanticModel semanticModel,
        IMethodSymbol originalMethod,
        List<ITypeParameterSymbol> required)
    {
        foreach (var typeOf in declaration.DescendantNodes().OfType<TypeOfExpressionSyntax>())
        {
            if (semanticModel.GetTypeInfo(typeOf.Type).Type is ITypeParameterSymbol typeParameter
                && typeParameter.DeclaringMethod == null
                && IsOwnedByMethodOrType(typeParameter, originalMethod))
            {
                AddUnique(required, typeParameter);
            }
        }

        // Scan default(T) expressions (the explicit form, not rewritten by GenericPreprocessor)
        foreach (var defaultExpr in declaration.DescendantNodes().OfType<DefaultExpressionSyntax>())
        {
            if (semanticModel.GetTypeInfo(defaultExpr.Type).Type is ITypeParameterSymbol typeParameter
                && typeParameter.DeclaringMethod == null
                && IsOwnedByMethodOrType(typeParameter, originalMethod))
            {
                AddUnique(required, typeParameter);
            }
        }

        // Scan bare 'default' literals (C# 7.1+). GenericPreprocessor rewrites
        // default(T) → default in safe contexts (assignment, return, etc.).
        // The target type is inferred from ConvertedType.
        foreach (var defaultLiteral in declaration.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.DefaultLiteralExpression)))
        {
            var typeInfo = semanticModel.GetTypeInfo(defaultLiteral);
            var targetType = typeInfo.ConvertedType ?? typeInfo.Type;
            if (targetType is ITypeParameterSymbol typeParameter
                && typeParameter.DeclaringMethod == null
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
            {
                // new T() where T is the class/method's own new()-constrained type
                // parameter: instantiation needs the runtime Class token so the Java
                // output can call Activator.createInstance(tClass).
                if (semanticModel.GetTypeInfo(creation).Type is ITypeParameterSymbol createdTypeParameter
                    && createdTypeParameter.HasConstructorConstraint
                    && IsOwnedByMethodOrType(createdTypeParameter, originalMethod))
                {
                    AddUnique(required, createdTypeParameter);
                }
                continue;
            }

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

        // GenericPreprocessor rewrites `new T()` (where T : new()) to
        // `System.Activator.CreateInstance<T>()` before the requirements scan runs.
        // The rewritten form is an InvocationExpressionSyntax, not a
        // BaseObjectCreationExpressionSyntax, so the loop above misses it.
        // Scan for Activator.CreateInstance<T>() calls and register the type
        // parameter as a runtime class requirement.
        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.ArgumentList.Arguments.Count != 0)
                continue;

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name is not GenericNameSyntax genericName)
                continue;

            if (genericName.Identifier.ValueText != "CreateInstance")
                continue;

            // Check that the receiver is System.Activator or Activator
            var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol == null)
            {
                // Fallback: check receiver text
                var receiverText = memberAccess.Expression.ToString();
                if (receiverText != "System.Activator" && receiverText != "Activator")
                    continue;
            }
            else if (methodSymbol.ContainingType?.ToDisplayString() != "System.Activator")
            {
                continue;
            }

            if (genericName.TypeArgumentList.Arguments.Count != 1)
                continue;

            var typeArgSymbol = semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type;
            if (typeArgSymbol is ITypeParameterSymbol typeParam
                && typeParam.HasConstructorConstraint
                && IsOwnedByMethodOrType(typeParam, originalMethod))
            {
                AddUnique(required, typeParam);
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

        // Also check TypeReference requirements (e.g. DeserializeWrapper<T> bodies
        // that call JsonSerializer.Deserialize<T> — the requirement is registered as
        // a TypeReference, not a Class<T>, so GetRequiredTypeParameters misses it).
        // Callers must forward the TypeReference token too.
        // Check both the invoked method and any override implementations
        // (abstract/virtual methods resolve to the base declaration, but the
        // requirement was registered on the override that has the body).
        var methodsToCheck = new List<IMethodSymbol> { invokedMethod.OriginalDefinition };
        // Find override implementations that may have registered TypeReference requirements.
        // Overrides can be in the containing type itself, in nested types, or in derived
        // types. We recursively search nested types (e.g. StringDeserializerWrapper inside
        // DeserializationWrapper) because GetMembers() does not descend into nested types.
        if (invokedMethod.ContainingType != null)
        {
            AddOverrideMethods(invokedMethod.ContainingType, invokedMethod.OriginalDefinition, methodsToCheck);
        }
        foreach (var methodToCheck in methodsToCheck)
        {
            var typeRefParamNames = context.Options.RuntimeClassParameters
                .GetMethodTypeReferenceParameterNames(methodToCheck);
            foreach (var typeRefParamName in typeRefParamNames)
            {
                var invokedTypeParameter = methodToCheck.TypeParameters
                    .FirstOrDefault(tp => tp.Name == typeRefParamName);
                if (invokedTypeParameter == null)
                    continue;
                var runtimeType = ResolveRuntimeTypeArgument(invokedTypeParameter, invokedMethod);
                if (runtimeType is ITypeParameterSymbol runtimeTypeParameter
                    && IsOwnedByMethodOrType(runtimeTypeParameter, originalMethod))
                {
                    AddUnique(required, runtimeTypeParameter);
                }
            }
        }
    }

    /// <summary>
    /// Recursively finds override implementations of <paramref name="targetMethod"/>
    /// within <paramref name="containingType"/> and all its nested types. This is
    /// needed because GetMembers() does not descend into nested type declarations,
    /// and override implementations are often in nested classes (e.g.
    /// StringDeserializerWrapper inside DeserializationWrapper).
    /// </summary>
    private static void AddOverrideMethods(
        INamedTypeSymbol containingType,
        IMethodSymbol targetMethod,
        List<IMethodSymbol> methodsToCheck)
    {
        foreach (var member in containingType.GetMembers())
        {
            if (member is IMethodSymbol method
                && method.IsOverride
                && !SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, targetMethod))
            {
                var overridden = method.OverriddenMethod;
                while (overridden != null)
                {
                    if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, targetMethod))
                    {
                        methodsToCheck.Add(method.OriginalDefinition);
                        break;
                    }
                    overridden = overridden.OverriddenMethod;
                }
            }

            // Recurse into nested types
            if (member is INamedTypeSymbol nestedType)
            {
                AddOverrideMethods(nestedType, targetMethod, methodsToCheck);
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

        // The current tree's model may belong to another file (class-level scans run
        // from any caller). Fall back to the project compilation.
        try
        {
            return context.ProjectCompilation?.GetSemanticModel(declaration.SyntaxTree);
        }
        catch
        {
            return null;
        }
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
                    return UnwrapNullableTypeArgument(typeSymbol.TypeArguments[i]);
            }
        }

        return null;
    }

    /// <summary>
    /// C# base types like JsonConverter&lt;T?&gt; pass the class's own type parameter
    /// wrapped in Nullable&lt;T&gt;. The runtime Class token is that of the underlying
    /// type argument, so unwrap Nullable before converting to a class argument.
    /// </summary>
    private static ITypeSymbol UnwrapNullableTypeArgument(ITypeSymbol typeArgument)
    {
        if (typeArgument is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
                TypeArguments.Length: 1
            } nullableArg)
        {
            return nullableArg.TypeArguments[0];
        }

        return typeArgument;
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
        // Unsigned/signed scalar values that map to the same Java type must
        // keep distinct reflection/type-token identity (e.g. ByteConverter vs
        // Int32Converter in JsonSerializerOptions' Dictionary<Type, JsonConverter>).
        switch (runtimeType.SpecialType)
        {
            case SpecialType.System_Byte:
                return "io.github.ningpp.compat.CSharpByte.class";
            case SpecialType.System_SByte:
                return "io.github.ningpp.compat.CSharpSByte.class";
            case SpecialType.System_UInt16:
                return "io.github.ningpp.compat.CSharpUInt16.class";
            case SpecialType.System_UInt32:
                return "io.github.ningpp.compat.CSharpUInt32.class";
            case SpecialType.System_UInt64:
                return "io.github.ningpp.compat.CSharpUInt64.class";
        }

        if (runtimeType is ITypeParameterSymbol typeParameter)
        {
            if (context.TryGetRuntimeClassParameter(typeParameter.Name, out var runtimeClassParameter))
            {
                return runtimeClassParameter;
            }
            // No runtime Class token available (e.g. the type parameter only feeds a
            // base constructor) — widen through null so super(...) still compiles.
            // T.class is illegal Java.
            return $"((Class<{typeParameter.Name}>) (Class<?>) null)";
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
