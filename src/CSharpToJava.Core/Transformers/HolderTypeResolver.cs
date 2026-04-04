namespace CSharpToJava.Core.Transformers;

/// <summary>
/// Centralized resolver for Java holder types used in ref/out parameter conversion.
/// Primitive types map to dedicated holders (IntHolder, LongHolder, etc.);
/// reference types use ObjectHolder&lt;T&gt;.
/// </summary>
public static class HolderTypeResolver
{
    /// <summary>
    /// Converts a Java type to its corresponding holder class name for ref/out parameters.
    /// Generated Java code must include holder class definitions (e.g., IntHolder, ObjectHolder&lt;T&gt;)
    /// in the runtime library that accompanies the converted sources.
    /// </summary>
    public static string GetHolderType(string javaType)
    {
        return javaType switch
        {
            "int" => "IntHolder",
            "long" => "LongHolder",
            "double" => "DoubleHolder",
            "float" => "FloatHolder",
            "boolean" => "BoolHolder",
            "char" => "CharHolder",
            "short" => "ShortHolder",
            "byte" => "ByteHolder",
            // Safety net: empty javaType can occur when the semantic model returns an
            // IErrorTypeSymbol for an unresolved type; fall back to Object to avoid
            // generating invalid Java like "ObjectHolder<>".
            _ when string.IsNullOrEmpty(javaType) => "ObjectHolder<Object>",
            _ => $"ObjectHolder<{javaType}>"
        };
    }

    /// <summary>
    /// Returns the Java constructor call for a holder type.
    /// Generic ObjectHolder&lt;T&gt; uses diamond type inference; primitive holders use default constructor.
    /// </summary>
    public static string GetHolderInstantiation(string holderType)
    {
        if (holderType.StartsWith("ObjectHolder<", System.StringComparison.Ordinal))
            return "new ObjectHolder<>()";
        return $"new {holderType}()";
    }

    /// <summary>
    /// Returns the Java constructor call for a holder initialized with a value.
    /// </summary>
    public static string GetHolderInstantiationWithValue(string holderType, string valueExpr)
    {
        if (holderType.StartsWith("ObjectHolder<", System.StringComparison.Ordinal))
            return $"new ObjectHolder<>({valueExpr})";
        return $"new {holderType}({valueExpr})";
    }

    /// <summary>
    /// Returns true if the given type name is a holder type (IntHolder, ObjectHolder&lt;T&gt;, etc.).
    /// </summary>
    public static bool IsHolderType(string typeName)
    {
        return typeName is "IntHolder" or "LongHolder" or "DoubleHolder" or "FloatHolder"
            or "BoolHolder" or "CharHolder" or "ShortHolder" or "ByteHolder"
            || typeName.StartsWith("ObjectHolder<", System.StringComparison.Ordinal);
    }
}
