using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using System.Text;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Transformers.Expression.Utilities;

/// <summary>
/// Static helper methods for expression transformation.
/// Shared utilities used across multiple transformer classes.
/// </summary>
public static class ExpressionTransformerHelpers
{
    private static readonly Regex _numericLiteralPattern = new Regex(
        @"^[+\-]?(0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*(\.\d[\d_]*)?)([uU][lL]?|[lL][uU]?|[fF]|[dD]|[mM])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns true if the expression string is an integer or float literal.
    /// Covers decimal, hex (0x...), binary (0b...), and unsigned (U/UL) suffixes.
    /// </summary>
    public static bool IsNumericLiteral(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        return _numericLiteralPattern.IsMatch(expr.Trim());
    }

    /// <summary>
    /// Checks if the type name is a .NET system primitive type.
    /// </summary>
    public static bool IsSystemPrimitiveType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return false;
        return typeName switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or
            "System.Int16" or "short" or "System.Byte" or "byte" or
            "System.SByte" or "sbyte" or
            "System.UInt32" or "uint" or "System.UInt64" or "ulong" or
            "System.UInt16" or "ushort" or
            "System.Single" or "float" or
            "System.Double" or "double" or "System.Boolean" or "bool" or
            "System.Char" or "char" or "System.String" or "string" or
            "System.Object" or "object" => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the type name is a C# basic primitive type.
    /// </summary>
    public static bool IsCSharpPrimitiveType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return false;
        return typeName switch
        {
            "int" or "double" or "float" or "long" or "short" or
            "byte" or "sbyte" or "char" or "bool" => true,
            _ => false
        };
    }

    /// <summary>
    /// Maps a C# PredefinedTypeSyntax (bool, int, ...) to the Java boxed class name.
    /// </summary>
    public static string BoxedTypeName(PredefinedTypeSyntax node)
    {
        return node.Keyword.Text switch
        {
            "bool" => "Boolean",
            "int" => "Integer",
            "long" => "Long",
            "double" => "Double",
            "float" => "Float",
            "char" => "Character",
            "short" => "Short",
            "byte" => "Byte",
            "string" => "String",
            "object" => "Object",
            _ => node.Keyword.Text
        };
    }

    /// <summary>
    /// Gets the Java wrapper type name for a C# primitive type.
    /// </summary>
    public static string GetJavaWrapperType(string csharpPrimitive)
    {
        return csharpPrimitive switch
        {
            "int" => "Integer",
            "double" => "Double",
            "float" => "Float",
            "long" => "Long",
            "short" => "Short",
            "byte" => "short",
            "sbyte" => "byte",
            "char" => "Character",
            "bool" => "Boolean",
            _ => csharpPrimitive
        };
    }

    /// <summary>
    /// Boxes a Java primitive type name to its wrapper class name.
    /// e.g. int → Integer, long → Long, boolean → Boolean.
    /// Returns the input unchanged if it is not a known Java primitive.
    /// </summary>
    public static string BoxJavaPrimitiveType(string javaType)
    {
        return javaType switch
        {
            "int"     => "Integer",
            "long"    => "Long",
            "double"  => "Double",
            "float"   => "Float",
            "short"   => "Short",
            "byte"    => "Byte",
            "char"    => "Character",
            "boolean" => "Boolean",
            _ => javaType
        };
    }

    /// <summary>
    /// Checks if the type name is a Java wrapper type.
    /// Note: 'Byte' maps to C# sbyte (signed); C# byte (unsigned) is mapped to 'Short'.
    /// </summary>
    public static bool IsJavaWrapperType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return false;
        return typeName switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or
            "System.Int16" or "short" or "System.Byte" or "byte" or
            "System.SByte" or "System.Single" or "float" or
            "System.Double" or "double" or "System.Boolean" or "bool" or
            "System.Char" or "char" or
            "Integer" or "Double" or "Float" or "Long" or
            "Short" or "Byte" or "Character" or "Boolean" => true,
            _ => false
        };
    }

    /// <summary>
    /// Converts a name to PascalCase.
    /// </summary>
    public static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpper(name[0]) + name.Substring(1);
    }

    /// <summary>
    /// Checks if a word is a Java keyword.
    /// </summary>
    public static bool IsJavaKeyword(string word)
    {
        return word switch
        {
            "abstract" or "assert" or "boolean" or "break" or "byte" or
            "case" or "catch" or "char" or "class" or "const" or
            "continue" or "default" or "do" or "double" or "else" or
            "enum" or "extends" or "final" or "finally" or "float" or
            "for" or "goto" or "if" or "implements" or "import" or
            "instanceof" or "int" or "interface" or "long" or "native" or
            "new" or "package" or "private" or "protected" or "public" or
            "return" or "short" or "static" or "strictfp" or "super" or
            "switch" or "synchronized" or "this" or "throw" or "throws" or
            "transient" or "try" or "void" or "volatile" or "while" or
            "true" or "false" or "null" => true,
            _ => false
        };
    }

    /// <summary>
    /// Strips ".collect(Collectors.toList())" or similar from a stream expression.
    /// </summary>
    public static string StripCollect(string target)
    {
        var patterns = new[]
        {
            ".collect(Collectors.toList())",
            ".collect(Collectors.toSet())",
            ".collect(Collectors.toCollection(ArrayList::new))",
            ".collect(Collectors.toCollection(LinkedList::new))",
            ".collect(Collectors.toCollection(HashSet::new))",
            ".collect(Collectors.toCollection LinkedHashSet::new)",
            ".collect(java.util.stream.Collectors.toList())",
            ".collect(java.util.stream.Collectors.toSet())",
            ".collect(java.util.stream.Collectors.toCollection(ArrayList::new))"
        };

        foreach (var pattern in patterns)
        {
            if (target.EndsWith(pattern))
            {
                return target.Substring(0, target.Length - pattern.Length);
            }
        }

        return target;
    }

    /// <summary>
    /// Checks if a target expression already has a .collect() call.
    /// </summary>
    public static bool IsAlreadyCollected(string target)
    {
        return target.Contains(".collect(");
    }

    /// <summary>
    /// Checks if a target expression is already collected (core logic).
    /// </summary>
    public static bool IsAlreadyCollectedCore(string t)
    {
        return t.Contains(".collect(Collectors.toList())") ||
               t.Contains(".collect(Collectors.toSet())") ||
               t.Contains(".collect(Collectors.toCollection(ArrayList::new))") ||
               t.Contains(".collect(Collectors.toCollection(LinkedList::new))") ||
               t.Contains(".collect(Collectors.toCollection(HashSet::new))") ||
               t.Contains(".collect(Collectors.toCollection(LinkedHashSet::new))");
    }

    /// <summary>
    /// Gets the default value for a struct type.
    /// </summary>
    public static string GetStructFieldDefault(ITypeSymbol type)
    {
        // For struct types, we need to return the default value representation
        var typeName = type.ToDisplayString();
        return typeName switch
        {
            "System.Int32" => "0",
            "System.Int64" => "0L",
            "System.Single" => "0.0f",
            "System.Double" => "0.0",
            "System.Boolean" => "false",
            "System.Char" => "'\\0'",
            _ => "default"
        };
    }

    /// <summary>
    /// Returns true if the given C# type is a dictionary/map type that maps to Java Map.
    /// Java Map has no .stream() method; entrySet().stream() must be used instead.
    /// </summary>
    public static bool IsDictionaryType(ITypeSymbol? type)
    {
        if (type == null) return false;
        var definition = type.OriginalDefinition?.ToDisplayString() ?? type.ToDisplayString();
        return definition is
            "System.Collections.Generic.Dictionary<TKey, TValue>" or
            "System.Collections.Generic.IDictionary<TKey, TValue>" or
            "System.Collections.Generic.SortedDictionary<TKey, TValue>" or
            "System.Collections.Generic.SortedList<TKey, TValue>" or
            "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>" or
            "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>" or
            "System.Collections.Hashtable";
    }

    /// <summary>
    /// Returns true if the given C# type maps to a Java Collection (i.e., can call .stream() directly).
    /// Excludes dictionary types (map to Java Map) and plain Iterable types (IEnumerable without ICollection).
    /// </summary>
    public static bool CanCallCollectionStream(ITypeSymbol? receiverType)
    {
        if (receiverType == null) return false;
        // Dictionary types map to Java Map which has no .stream()
        if (IsDictionaryType(receiverType)) return false;
        if (receiverType.OriginalDefinition?.ToDisplayString() is
            "System.Collections.Generic.ICollection<T>" or "System.Collections.ICollection")
            return true;
        if (receiverType is not INamedTypeSymbol namedType) return false;
        return !IsDictionaryType(namedType)
            && namedType.AllInterfaces.Any(i =>
                i.OriginalDefinition?.ToDisplayString() is
                    "System.Collections.Generic.ICollection<T>" or "System.Collections.ICollection");
    }

    /// <summary>
    /// Builds a Java stream source expression for the given C# collection/iterable expression.
    /// Priority: array → Arrays.stream | IGrouping → getValue().stream()
    ///           Dictionary → entrySet().stream() | Collection → .stream()
    ///           Iterable/IEnumerable → StreamSupport.stream(spliterator, false)
    ///           null type → .stream() (safe fallback for Collection receivers)
    /// </summary>
    public static string BuildStreamExpression(
        string receiverExpr,
        ITypeSymbol? receiverType,
        ConversionContext context,
        bool boxPrimitiveArrayElements = false,
        bool preserveGroupingValueStream = false)
    {
        if (receiverType is IArrayTypeSymbol arrayType)
        {
            context.AddImport("java.util.Arrays");
            return boxPrimitiveArrayElements && arrayType.ElementType.IsValueType
                ? $"Arrays.stream({receiverExpr}).boxed()"
                : $"Arrays.stream({receiverExpr})";
        }

        if (preserveGroupingValueStream
            && receiverType is INamedTypeSymbol groupingType
            && groupingType.OriginalDefinition?.ToDisplayString().StartsWith("System.Linq.IGrouping<") == true)
        {
            return $"{receiverExpr}.getValue().stream()";
        }

        if (IsDictionaryType(receiverType))
            return $"{receiverExpr}.entrySet().stream()";

        if (CanCallCollectionStream(receiverType))
            return $"{receiverExpr}.stream()";

        if (receiverType != null)
        {
            // IEnumerable<T> → java.lang.Iterable<T> — no .stream(); use StreamSupport
            context.AddImport("java.util.stream.StreamSupport");
            return $"StreamSupport.stream({receiverExpr}.spliterator(), false)";
        }

        // Type unknown: fall back to .stream() (correct for Collection; may require manual fix for bare Iterable)
        return $"{receiverExpr}.stream()";
    }
}
