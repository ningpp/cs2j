using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Context;

/// <summary>
/// Kind of type-erasure conflict between two method overloads.
/// </summary>
public enum ErasureConflictKind
{
    None,
    DifferentTypeParamCount,
    SameTypeParamCount
}

/// <summary>
/// Static utility methods for Java naming conventions (keyword escaping, type erasure conflict detection).
/// Extracted from ConversionContext.
/// </summary>
public static class JavaNaming
{
    public static bool IsJavaKeyword(string word)
    {
        return word switch
        {
            "abstract" or "assert" or "boolean" or "break" or "byte" or "case" or "catch" or
            "char" or "class" or "const" or "continue" or "default" or "do" or "double" or
            "else" or "enum" or "extends" or "final" or "finally" or "float" or "for" or
            "goto" or "if" or "implements" or "import" or "instanceof" or "int" or
            "interface" or "long" or "native" or "new" or "package" or "private" or
            "protected" or "public" or "return" or "short" or "static" or "strictfp" or
            "super" or "switch" or "synchronized" or "this" or "throw" or "throws" or
            "transient" or "try" or "void" or "volatile" or "while"
            or "true" or "false" or "null" => true,
            _ => false
        };
    }

    public static string EscapeJavaKeyword(string word)
    {
        return IsJavaKeyword(word) ? word + "Value" : word;
    }

    /// <summary>
    /// Determines whether a C# method has a type-erasure conflict with another overload,
    /// and returns the kind of conflict.
    /// </summary>
    public static (bool HasConflict, ErasureConflictKind Kind) GetErasureConflictKind(IMethodSymbol method)
    {
        if (method.ContainingType == null) return (false, ErasureConflictKind.None);
        if (method.ContainingType.DeclaringSyntaxReferences.Length == 0) return (false, ErasureConflictKind.None);

        foreach (var sibling in method.ContainingType.GetMembers().OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(sibling, method)) continue;
            if (sibling.Name != method.Name) continue;
            if (sibling.Parameters.Length != method.Parameters.Length) continue;
            if (!HaveSameErasedParameters(method, sibling)) continue;

            var kind = sibling.TypeParameters.Length == method.TypeParameters.Length
                ? ErasureConflictKind.SameTypeParamCount
                : ErasureConflictKind.DifferentTypeParamCount;

            if (kind == ErasureConflictKind.DifferentTypeParamCount)
            {
                if (method.TypeParameters.Length < sibling.TypeParameters.Length)
                    return (true, kind);
            }
            else
            {
                // Both methods are in conflict. Determine which one should be renamed
                // by source order: the first one keeps original name.
                var myLoc = method.Locations.FirstOrDefault(l => l.SourceTree != null);
                var sibLoc = sibling.Locations.FirstOrDefault(l => l.SourceTree != null);
                if (myLoc != null && sibLoc != null)
                {
                    var myLine = myLoc.GetLineSpan().StartLinePosition.Line;
                    var sibLine = sibLoc.GetLineSpan().StartLinePosition.Line;
                    if (myLine > sibLine) return (true, kind);
                }
            }
        }

        return (false, ErasureConflictKind.None);
    }

    /// <summary>
    /// For SameTypeParamCount conflicts, computes a stable 1-based index within the
    /// conflict group (sorted by source line). Returns 1 for the first method (keeps
    /// original name), 2+ for subsequent methods that need _erasure_N suffix.
    /// </summary>
    public static int GetErasureConflictIndex(IMethodSymbol method)
    {
        if (method.ContainingType == null) return 1;

        var conflictGroup = method.ContainingType.GetMembers().OfType<IMethodSymbol>()
            .Where(s => s.Name == method.Name
                && s.Parameters.Length == method.Parameters.Length
                && s.TypeParameters.Length == method.TypeParameters.Length
                && HaveSameErasedParameters(method, s))
            .Select(s => new
            {
                Symbol = s,
                Line = s.Locations.FirstOrDefault(l => l.SourceTree != null)
                    ?.GetLineSpan().StartLinePosition.Line ?? 0
            })
            .OrderBy(x => x.Line)
            .ToList();

        if (conflictGroup.Count <= 1) return 1;

        for (int i = 0; i < conflictGroup.Count; i++)
        {
            if (SymbolEqualityComparer.Default.Equals(conflictGroup[i].Symbol, method))
                return i + 1;
        }

        return 1;
    }

    /// <summary>
    /// Returns the suffix to append to a method name when it has an erasure conflict,
    /// or an empty string if no conflict exists.
    /// </summary>
    public static string GetErasureConflictSuffix(IMethodSymbol method)
    {
        var (hasConflict, kind) = GetErasureConflictKind(method);
        if (!hasConflict) return "";

        return kind switch
        {
            ErasureConflictKind.DifferentTypeParamCount => $"_{method.TypeParameters.Length}tp",
            ErasureConflictKind.SameTypeParamCount => $"_erasure_{GetErasureConflictIndex(method)}",
            _ => ""
        };
    }

    /// <summary>
    /// Checks whether a C# method has a type-erasure conflict with another overload in the
    /// same type: same name, same erased parameter types, but different generic type parameter
    /// counts. Returns true for the overload with FEWER type parameters.
    /// </summary>
    public static bool HasTypeErasureConflict(IMethodSymbol method)
    {
        if (method.ContainingType == null) return false;
        if (method.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        foreach (var sibling in method.ContainingType.GetMembers().OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(sibling, method)) continue;
            if (sibling.Name != method.Name) continue;
            if (sibling.Parameters.Length != method.Parameters.Length) continue;
            if (sibling.TypeParameters.Length == method.TypeParameters.Length) continue;
            if (HaveSameErasedParameters(method, sibling))
                return method.TypeParameters.Length < sibling.TypeParameters.Length;
        }
        return false;
    }

    public static string GetErasureRenamedSuffix(int typeParameterCount)
        => $"_{typeParameterCount}tp";

    private static bool HaveSameErasedParameters(IMethodSymbol a, IMethodSymbol b)
    {
        for (int i = 0; i < a.Parameters.Length; i++)
        {
            if (GetErasedTypeName(a.Parameters[i].Type) != GetErasedTypeName(b.Parameters[i].Type))
                return false;
        }
        return true;
    }

    private static string GetErasedTypeName(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => "System.Object",
        IArrayTypeSymbol arr => GetErasedTypeName(arr.ElementType) + "[]",
        INamedTypeSymbol named => named.OriginalDefinition.ContainingNamespace + "." + named.OriginalDefinition.Name,
        _ => type.ToDisplayString()
    };

    /// <summary>
    /// Checks whether a method in a derived class has a cross-inheritance type-erasure conflict.
    /// Returns the name of the base-class method if the method's erased parameter signature
    /// matches a base-class method with different generic type arguments.
    /// </summary>
    public static string? FindCrossInheritanceErasureConflict(IMethodSymbol method)
    {
        if (method.ContainingType?.BaseType == null) return null;

        var erasedSig = GetErasedSignature(method);

        var baseType = method.ContainingType.BaseType;
        while (baseType != null)
        {
            foreach (var baseMember in baseType.GetMembers().OfType<IMethodSymbol>())
            {
                if (baseMember.Name != method.Name) continue;
                if (baseMember.Parameters.Length != method.Parameters.Length) continue;
                if (baseMember.DeclaredAccessibility == Accessibility.Private) continue;

                var baseErasedSig = GetErasedSignature(baseMember);
                if (erasedSig == baseErasedSig && !HaveSameActualParameters(method, baseMember))
                {
                    return $"{baseType.Name}.{baseMember.Name}";
                }
            }
            baseType = baseType.BaseType;
        }

        return null;
    }

    private static string GetErasedSignature(IMethodSymbol method)
        => string.Join(",", method.Parameters.Select(p => GetErasedTypeName(p.Type)));

    private static bool HaveSameActualParameters(IMethodSymbol a, IMethodSymbol b)
    {
        for (int i = 0; i < a.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(a.Parameters[i].Type, b.Parameters[i].Type))
                return false;
        }
        return true;
    }
}
