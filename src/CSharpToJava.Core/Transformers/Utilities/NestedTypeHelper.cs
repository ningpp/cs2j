using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Utilities;

/// <summary>
/// Helper for determining whether a nested type should be static in Java.
/// In Java, a non-static inner class has an implicit reference to the enclosing instance.
/// This is only needed when the nested type accesses instance members of the enclosing type.
/// Type-parameter-only references do NOT require a non-static inner class, because
/// Java static nested classes can have their own type parameters.
/// </summary>
internal static class NestedTypeHelper
{
    /// <summary>
    /// Determines whether a nested type should be static in Java.
    /// Returns true if the nested type does NOT need access to the enclosing class's
    /// instance (safe to be static), false if it does (must be non-static).
    ///
    /// A nested type that only references the enclosing type's type parameters (not
    /// instance members) can be static in Java, because Java static nested classes
    /// can have their own type parameters. This is important for array creation —
    /// Java cannot create arrays of non-static inner classes.
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

        // A nested type that only references the enclosing type's type parameters
        // (not instance members) can be static in Java. Java static nested classes
        // can have their own type parameters. This is critical for array creation —
        // Java cannot create arrays of non-static inner classes.
        //
        // Type-parameter-only references are NOT sufficient to require a non-static
        // inner class. Only actual instance member access (calling methods, reading
        // fields of the enclosing instance) requires a non-static inner class.
        // Since we cannot easily detect instance member access from the symbol model
        // alone, and type-parameter references are the common case, we default to
        // making the nested type static when it has its own type parameters that shadow
        // the enclosing type's, or when it only references the enclosing type's type
        // parameters through its members (not through instance field access).
        return !ReferencesNonTypeParamEnclosingMembers(nestedSymbol, enclosingTypeParams);
    }

    /// <summary>
    /// Gets the type parameters from the enclosing type that the nested type references
    /// but doesn't declare as its own. These parameters need to be added to the nested
    /// type's own type parameter list when making it static in Java.
    /// </summary>
    public static List<ITypeParameterSymbol> GetEnclosingTypeParamsUsedByNested(
        SyntaxNode nestedTypeDecl,
        ConversionContext context)
    {
        var result = new List<ITypeParameterSymbol>();
        var nestedSymbol = context.GetDeclaredSymbol(nestedTypeDecl) as INamedTypeSymbol;
        if (nestedSymbol == null)
            return result;

        var enclosingType = nestedSymbol.ContainingType;
        if (enclosingType == null || enclosingType.TypeParameters.IsEmpty)
            return result;

        var enclosingTypeParams = new HashSet<ITypeParameterSymbol>(
            enclosingType.TypeParameters.Where(tp => tp.DeclaringMethod == null),
            SymbolEqualityComparer.Default);

        if (enclosingTypeParams.Count == 0)
            return result;

        // Collect names of the nested type's own type parameters
        var nestedOwnTypeParamNames = new HashSet<string>(
            nestedSymbol.TypeParameters.Select(tp => tp.Name),
            StringComparer.Ordinal);

        // Find enclosing type params that are referenced but not shadowed by the nested type's own
        foreach (var enclosingParam in enclosingTypeParams)
        {
            if (nestedOwnTypeParamNames.Contains(enclosingParam.Name))
                continue; // Shadowed by the nested type's own parameter

            if (NestedTypeReferencesTypeParam(nestedSymbol, enclosingParam))
                result.Add(enclosingParam);
        }

        return result;
    }

    /// <summary>
    /// Checks if any member of the nested type references the given type parameter.
    /// </summary>
    private static bool NestedTypeReferencesTypeParam(INamedTypeSymbol nestedSymbol, ITypeParameterSymbol typeParam)
    {
        foreach (var member in nestedSymbol.GetMembers())
        {
            if (member.IsStatic)
                continue;

            foreach (var type in GetMemberTypes(member))
            {
                if (TypeReferencesTypeParam(type, typeParam))
                    return true;
            }
        }

        if (nestedSymbol.BaseType != null && TypeReferencesTypeParam(nestedSymbol.BaseType, typeParam))
            return true;

        foreach (var iface in nestedSymbol.Interfaces)
        {
            if (TypeReferencesTypeParam(iface, typeParam))
                return true;
        }

        return false;
    }

    private static bool TypeReferencesTypeParam(ITypeSymbol type, ITypeParameterSymbol targetParam)
    {
        if (type is ITypeParameterSymbol tp && SymbolEqualityComparer.Default.Equals(tp, targetParam))
            return true;

        if (type is INamedTypeSymbol named && named.TypeArguments.Length > 0)
        {
            foreach (var arg in named.TypeArguments)
            {
                if (TypeReferencesTypeParam(arg, targetParam))
                    return true;
            }
        }

        if (type is IArrayTypeSymbol array)
            return TypeReferencesTypeParam(array.ElementType, targetParam);

        return false;
    }

    /// <summary>
    /// Checks whether the nested type references the enclosing type's members in a way
    /// that requires a non-static inner class. Type-parameter-only references do NOT
    /// require a non-static inner class — Java static nested classes can have their own
    /// type parameters.
    ///
    /// Since detecting actual instance member access from the symbol model is complex,
    /// we use a heuristic: if the nested type has its own type parameters that shadow
    /// the enclosing type's, it doesn't need the enclosing instance. If it doesn't have
    /// its own type parameters but references the enclosing type's, we still make it
    /// static because the type parameters can be propagated as the nested type's own
    /// in Java.
    /// </summary>
    private static bool ReferencesNonTypeParamEnclosingMembers(
        INamedTypeSymbol nestedSymbol,
        HashSet<ITypeParameterSymbol> enclosingTypeParams)
    {
        // With the new approach, we always make nested types static when they only
        // reference type parameters. The GetEnclosingTypeParamsUsedByNested method
        // is used to determine which type parameters need to be added to the nested
        // type's own parameter list.
        //
        // We only return true (needs non-static) if the nested type has members that
        // access the enclosing type's instance members (not just type parameters).
        // For now, we conservatively assume that type-parameter-only references don't
        // need a non-static inner class, which covers the common case of nested
        // collection entry types like LowLevelDictionary.Entry.
        return false;
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
