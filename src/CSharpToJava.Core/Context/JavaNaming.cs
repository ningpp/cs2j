using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Context;

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
            "transient" or "try" or "void" or "volatile" or "while" => true,
            _ => false
        };
    }

    public static string EscapeJavaKeyword(string word)
    {
        return IsJavaKeyword(word) ? word + "Value" : word;
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
}
