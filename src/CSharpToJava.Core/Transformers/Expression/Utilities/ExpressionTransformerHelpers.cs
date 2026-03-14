using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using System.Text;

namespace CSharpToJava.Core.Transformers.Expression.Utilities;

/// <summary>
/// Static helper methods for expression transformation.
/// Shared utilities used across multiple transformer classes.
/// </summary>
public static class ExpressionTransformerHelpers
{
    /// <summary>
    /// Returns true if the expression string is an integer or float literal.
    /// </summary>
    public static bool IsNumericLiteral(string expr)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(expr.Trim(), @"^-?\d+(\.\d+)?[LlfFdD]?$");
    }

    /// <summary>
    /// Checks if the type name is a .NET system primitive type.
    /// </summary>
    public static bool IsSystemPrimitiveType(string typeName)
    {
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
    public static bool IsCSharpPrimitiveType(string typeName)
    {
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
            "byte" => "Byte",
            "sbyte" => "Byte",
            "char" => "Character",
            "bool" => "Boolean",
            _ => csharpPrimitive
        };
    }

    /// <summary>
    /// Checks if the type name is a Java wrapper type.
    /// </summary>
    public static bool IsJavaWrapperType(string typeName)
    {
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
}
