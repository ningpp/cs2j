namespace CSharpToJava.Core.Transformers;

/// <summary>
/// Centralized resolver for Java holder types used in ref/out parameter conversion.
/// Primitive types map to dedicated holders (IntHolder, LongHolder, etc.);
/// reference types use io.github.ningpp.compat.ObjectHolder&lt;T&gt;.
/// Fully-qualified names are used to avoid shadowing by user-defined nested types
/// (e.g. a C# class named ObjectHolder inside the converted file).
/// </summary>
public static class HolderTypeResolver
{
    private const string CompatPackage = "io.github.ningpp.compat";

    /// <summary>
    /// Converts a Java type to its corresponding fully-qualified holder class name for ref/out parameters.
    /// Accepts both primitive names (int, boolean, ...) and boxed wrapper names (Integer, Boolean, ...).
    /// </summary>
    public static string GetHolderType(string javaType)
    {
        return javaType switch
        {
            "int" or "Integer" => $"{CompatPackage}.IntHolder",
            "long" or "Long" => $"{CompatPackage}.LongHolder",
            "double" or "Double" => $"{CompatPackage}.DoubleHolder",
            "float" or "Float" => $"{CompatPackage}.FloatHolder",
            "boolean" or "Boolean" => $"{CompatPackage}.BoolHolder",
            "char" or "Character" => $"{CompatPackage}.CharHolder",
            "short" or "Short" => $"{CompatPackage}.ShortHolder",
            "byte" or "Byte" => $"{CompatPackage}.ByteHolder",
            // Safety net: empty javaType can occur when the semantic model returns an
            // IErrorTypeSymbol for an unresolved type; fall back to Object to avoid
            // generating invalid Java like "ObjectHolder<>".
            _ when string.IsNullOrEmpty(javaType) => $"{CompatPackage}.ObjectHolder<Object>",
            _ => $"{CompatPackage}.ObjectHolder<{javaType}>"
        };
    }

    /// <summary>
    /// Returns the Java constructor call for a holder type.
    /// Generic ObjectHolder&lt;T&gt; uses diamond type inference; primitive holders use default constructor.
    /// Accepts both simple and fully-qualified holder type names.
    /// </summary>
    public static string GetHolderInstantiation(string holderType)
    {
        if (IsObjectHolderType(holderType))
            return $"new {CompatPackage}.ObjectHolder<>()";
        return $"new {holderType}()";
    }

    /// <summary>
    /// Returns the Java constructor call for a holder initialized with a value.
    /// Accepts both simple and fully-qualified holder type names.
    /// </summary>
    public static string GetHolderInstantiationWithValue(string holderType, string valueExpr)
    {
        if (IsObjectHolderType(holderType))
            return $"new {CompatPackage}.ObjectHolder<>({valueExpr})";
        return $"new {holderType}({valueExpr})";
    }

    private static bool IsObjectHolderType(string holderType)
    {
        return holderType == "ObjectHolder"
            || holderType.StartsWith("ObjectHolder<", System.StringComparison.Ordinal)
            || holderType.StartsWith($"{CompatPackage}.ObjectHolder<", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true if the given type name is a holder type (IntHolder, ObjectHolder&lt;T&gt;, etc.).
    /// Accepts both simple and fully-qualified names.
    /// </summary>
    public static bool IsHolderType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return false;

        var simpleName = typeName.StartsWith($"{CompatPackage}.", System.StringComparison.Ordinal)
            ? typeName.Substring($"{CompatPackage}.".Length)
            : typeName;

        return simpleName is "IntHolder" or "LongHolder" or "DoubleHolder" or "FloatHolder"
            or "BoolHolder" or "CharHolder" or "ShortHolder" or "ByteHolder"
            || simpleName.StartsWith("ObjectHolder<", System.StringComparison.Ordinal);
    }
}
