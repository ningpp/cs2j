using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Utilities;

/// <summary>
/// Helper for determining whether a nested type should be static in Java.
/// In Java, a static inner class cannot reference the enclosing class's type parameters.
/// When a C# nested type uses the enclosing type's type parameters, it must be
/// translated as a non-static inner class in Java.
/// </summary>
internal static class NestedTypeHelper
{
    /// <summary>
    /// Determines whether a nested type should be static in Java.
    /// Returns true if the nested type does NOT reference any of the enclosing type's
    /// type parameters (safe to be static), false if it does (must be non-static).
    /// </summary>
    public static bool ShouldBeStaticInJava(SyntaxNode nestedTypeDecl, ConversionContext context)
    {
        var nestedSymbol = context.GetDeclaredSymbol(nestedTypeDecl) as INamedTypeSymbol;
        if (nestedSymbol == null)
            return true; // No symbol info, default to static

        var enclosingType = nestedSymbol.ContainingType;
        if (enclosingType == null || enclosingType.TypeParameters.IsEmpty)
            return true; // Enclosing type has no type parameters, safe to be static

        // Collect class-level type parameters from the enclosing type
        var enclosingTypeParams = new HashSet<ITypeParameterSymbol>(
            enclosingType.TypeParameters.Where(tp => tp.DeclaringMethod == null),
            SymbolEqualityComparer.Default);

        if (enclosingTypeParams.Count == 0)
            return true;

        // Check if the nested type's members reference any enclosing type parameters
        foreach (var member in nestedSymbol.GetMembers())
        {
            foreach (var type in GetMemberTypes(member))
            {
                if (TypeReferencesEnclosingTypeParam(type, enclosingTypeParams))
                    return false;
            }
        }

        // Also check base type and implemented interfaces
        if (nestedSymbol.BaseType != null &&
            TypeReferencesEnclosingTypeParam(nestedSymbol.BaseType, enclosingTypeParams))
            return false;

        foreach (var iface in nestedSymbol.Interfaces)
        {
            if (TypeReferencesEnclosingTypeParam(iface, enclosingTypeParams))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the type symbols referenced by a member (field type, property type,
    /// method return/parameter types, event type).
    /// </summary>
    private static IEnumerable<ITypeSymbol> GetMemberTypes(ISymbol member)
    {
        switch (member)
        {
            case IFieldSymbol field:
                yield return field.Type;
                break;
            case IPropertySymbol property:
                yield return property.Type;
                break;
            case IEventSymbol eventSymbol:
                yield return eventSymbol.Type;
                break;
            case IMethodSymbol method:
                // Skip property getters/setters and event add/remove — they're covered above
                if (method.AssociatedSymbol != null)
                    yield break;
                yield return method.ReturnType;
                foreach (var param in method.Parameters)
                    yield return param.Type;
                break;
        }
    }

    /// <summary>
    /// Recursively checks whether a type symbol references any of the enclosing
    /// type parameters. Handles generic type arguments, array element types, etc.
    /// </summary>
    private static bool TypeReferencesEnclosingTypeParam(
        ITypeSymbol type,
        HashSet<ITypeParameterSymbol> enclosingTypeParams)
    {
        if (type is ITypeParameterSymbol tp && enclosingTypeParams.Contains(tp))
            return true;

        if (type is INamedTypeSymbol named && named.TypeArguments.Length > 0)
        {
            foreach (var arg in named.TypeArguments)
            {
                if (TypeReferencesEnclosingTypeParam(arg, enclosingTypeParams))
                    return true;
            }
        }

        if (type is IArrayTypeSymbol array)
            return TypeReferencesEnclosingTypeParam(array.ElementType, enclosingTypeParams);

        return false;
    }
}
