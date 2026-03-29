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
    /// Adapts an expression to its Roslyn ConvertedType when Java requires an explicit numeric
    /// conversion or a target-typed literal suffix to preserve C# implicit conversion semantics.
    /// </summary>
    public static string AdaptExpressionToConvertedType(
        ExpressionSyntax expression,
        string transformedExpression,
        ConversionContext context)
    {
        if (context.SemanticModel == null)
            return transformedExpression;

        var typeInfo = context.SemanticModel.GetTypeInfo(expression);
        return AdaptExpressionToTargetTypeCore(
            expression,
            transformedExpression,
            typeInfo.Type,
            typeInfo.ConvertedType);
    }

    /// <summary>
    /// Adapts an expression to a known target type when Java needs an explicit numeric conversion.
    /// This is used for assignments, indexers, method arguments, object initializers, and returns.
    /// </summary>
    public static string AdaptExpressionToTargetType(
        ExpressionSyntax expression,
        string transformedExpression,
        ITypeSymbol? targetType,
        ConversionContext context)
    {
        if (context.SemanticModel == null || targetType == null)
            return transformedExpression;

        var sourceType = context.SemanticModel.GetTypeInfo(expression).Type;
        return AdaptExpressionToTargetTypeCore(
            expression,
            transformedExpression,
            sourceType,
            targetType);
    }

    private static string AdaptExpressionToTargetTypeCore(
        ExpressionSyntax expression,
        string transformedExpression,
        ITypeSymbol? sourceType,
        ITypeSymbol? targetType)
    {
        if (sourceType == null || targetType == null)
            return transformedExpression;

        sourceType = UnwrapNullable(sourceType);
        targetType = UnwrapNullable(targetType);

        if (sourceType == null || targetType == null)
            return transformedExpression;

        if (SymbolEqualityComparer.Default.Equals(sourceType, targetType))
            return transformedExpression;

        var sourceSpecial = sourceType.SpecialType;
        var targetSpecial = targetType.SpecialType;

        if (!IsNumericOrCharType(sourceSpecial) || !IsNumericOrCharType(targetSpecial))
            return transformedExpression;

        if (TryRewriteNumericLiteral(expression, targetSpecial, out var rewrittenLiteral))
            return rewrittenLiteral;

        var castKeyword = targetSpecial switch
        {
            SpecialType.System_Byte or SpecialType.System_SByte => "byte",
            SpecialType.System_Int16 or SpecialType.System_UInt16 => "short",
            SpecialType.System_Int32 or SpecialType.System_UInt32 => "int",
            SpecialType.System_Int64 or SpecialType.System_UInt64 => "long",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Char => "char",
            _ => null
        };

        return castKeyword == null
            ? transformedExpression
            : $"({castKeyword}) ({transformedExpression})";
    }

    private static ITypeSymbol? UnwrapNullable(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableType)
            return nullableType.TypeArguments[0];

        return type;
    }

    private static bool IsNumericOrCharType(SpecialType specialType)
    {
        return specialType is SpecialType.System_Byte
            or SpecialType.System_SByte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_Char;
    }

    private static bool TryRewriteNumericLiteral(
        ExpressionSyntax expression,
        SpecialType targetSpecialType,
        out string rewrittenLiteral)
    {
        if (!TryGetSignedDecimalIntegralLiteral(expression, out var signedLiteral))
        {
            rewrittenLiteral = string.Empty;
            return false;
        }

        rewrittenLiteral = targetSpecialType switch
        {
            SpecialType.System_Int64 or SpecialType.System_UInt64 => signedLiteral + "L",
            SpecialType.System_Single => signedLiteral + "f",
            SpecialType.System_Double => signedLiteral + ".0",
            _ => string.Empty
        };

        return !string.IsNullOrEmpty(rewrittenLiteral);
    }

    private static bool TryGetSignedDecimalIntegralLiteral(ExpressionSyntax expression, out string signedLiteral)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression):
                return TryNormalizeDecimalIntegralLiteral(literal.Token.Text, out signedLiteral);

            case PrefixUnaryExpressionSyntax prefix
                when prefix.IsKind(SyntaxKind.UnaryMinusExpression) || prefix.IsKind(SyntaxKind.UnaryPlusExpression):
                if (TryGetSignedDecimalIntegralLiteral(prefix.Operand, out var operandLiteral))
                {
                    signedLiteral = prefix.IsKind(SyntaxKind.UnaryMinusExpression)
                        ? operandLiteral.StartsWith("-", StringComparison.Ordinal) ? operandLiteral : "-" + operandLiteral
                        : operandLiteral.TrimStart('+');
                    return true;
                }
                break;
        }

        signedLiteral = string.Empty;
        return false;
    }

    private static bool TryNormalizeDecimalIntegralLiteral(string tokenText, out string normalizedLiteral)
    {
        if (string.IsNullOrWhiteSpace(tokenText))
        {
            normalizedLiteral = string.Empty;
            return false;
        }

        var cleaned = tokenText.Replace("_", "", StringComparison.Ordinal);
        cleaned = cleaned.TrimEnd('u', 'U', 'l', 'L');

        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || cleaned.StartsWith("0b", StringComparison.OrdinalIgnoreCase)
            || cleaned.Contains('.', StringComparison.Ordinal)
            || cleaned.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            normalizedLiteral = string.Empty;
            return false;
        }

        if (cleaned.Length == 0 || !cleaned.All(char.IsDigit))
        {
            normalizedLiteral = string.Empty;
            return false;
        }

        normalizedLiteral = cleaned;
        return true;
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
            ".collect(Collectors.toList())",
            ".collect(Collectors.toSet())",
            ".collect(Collectors.toCollection(ArrayList::new))"
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
        bool preserveGroupingValueStream = false,
        ExpressionSyntax? receiverSyntaxNode = null)
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

        // System.Collections.Generic.LinkedList<T> maps to custom runtime type in this project,
        // which does not expose java.util.Collection#stream(). Use StreamSupport for safety.
        if (receiverType is INamedTypeSymbol linkedListType
            && linkedListType.OriginalDefinition?.ToDisplayString() == "System.Collections.Generic.LinkedList<T>")
        {
            context.AddImport("java.util.stream.StreamSupport");
            return $"StreamSupport.stream({receiverExpr}.spliterator(), false)";
        }

        if (CanCallCollectionStream(receiverType))
        {
            if (receiverType is INamedTypeSymbol named
                && named.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) != true)
            {
                // Custom project collections frequently map to custom Java types that do not
                // implement java.util.Collection#stream(); use StreamSupport for safety.
                context.AddImport("java.util.stream.StreamSupport");
                return $"StreamSupport.stream({receiverExpr}.spliterator(), false)";
            }
            return $"{receiverExpr}.stream()";
        }

        if (receiverType != null)
        {
            // IEnumerable<T> → java.lang.Iterable<T> — no .stream(); use StreamSupport
            context.AddImport("java.util.stream.StreamSupport");
            return $"StreamSupport.stream({receiverExpr}.spliterator(), false)";
        }

        // Type unknown: LINQ receiver is expected to be enumerable; prefer StreamSupport to
        // avoid requiring a concrete .stream() method on custom collection implementations.
        context.AddImport("java.util.stream.StreamSupport");
        return $"StreamSupport.stream({receiverExpr}.spliterator(), false)";
    }

    /// <summary>
    /// Checks whether the given expression resolves to a method or property whose original
    /// (unsubstituted) return type is T[] where T is a type parameter. Such methods/properties
    /// are converted to return List&lt;T&gt; in Java, so callers must use .stream() instead of Arrays.stream().
    /// </summary>
    /// <param name="requireActualTypeParam">When true, also checks that the actual (instantiated) return type
    /// has a non-primitive element (reference types are converted to List&lt;T&gt;, but primitive arrays like
    /// int[] stay as-is). Use true for element access/length/assignment to avoid false positives on
    /// primitive arrays. Use false for stream expressions where the Java method returns List&lt;T&gt;
    /// regardless of the call-site instantiation.</param>
    public static bool IsExpressionFromTypeParameterArrayReturn(ExpressionSyntax expr, ConversionContext context, bool requireActualTypeParam = false)
    {
        if (context.SemanticModel == null)
            return false;

        // Unwrap invocations: expr could be a method call like getAllIntersecting(rect)
        var symbolInfo = context.SemanticModel.GetSymbolInfo(expr);
        var symbol = symbolInfo.Symbol;

        if (symbol is IMethodSymbol method)
        {
            if (IsMethodReturningConvertedTypeParamArray(method, requireActualTypeParam))
                return true;
        }

        if (symbol is IPropertySymbol prop)
        {
            if (IsPropertyReturningConvertedTypeParamArray(prop, requireActualTypeParam))
                return true;
        }

        // For local variables, trace back to the initializer expression
        if (symbol is ILocalSymbol local)
        {
            // If the local's own type has a type-parameter element, it IS a type-param array
            // (e.g. TEdge[] inside BasicGraphOnEdges<TEdge> → List<TEdge> in Java)
            if (local.Type is IArrayTypeSymbol localArr
                && localArr.Rank == 1
                && localArr.ElementType.TypeKind == TypeKind.TypeParameter)
            {
                return true;
            }

            var declRef = local.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef?.GetSyntax() is VariableDeclaratorSyntax declarator)
            {
                // If the declaration uses an explicit concrete array type (e.g. Point[] vts = ...),
                // the converter materializes it as an actual Java array (adds .toArray()),
                // so later usage should treat it as an array, not a List.
                if (declarator.Parent is VariableDeclarationSyntax varDecl
                    && varDecl.Type is ArrayTypeSyntax
                    && local.Type is IArrayTypeSymbol explicitArr
                    && explicitArr.ElementType.TypeKind != TypeKind.TypeParameter)
                {
                    return false;
                }

                // For 'var' declarations or type-parameter arrays, trace to initializer
                if (declarator.Initializer?.Value != null)
                {
                    return IsExpressionFromTypeParameterArrayReturn(declarator.Initializer.Value, context, requireActualTypeParam);
                }
            }
        }

        // For member access expressions like tree.getAllIntersecting, check the name part
        if (expr is MemberAccessExpressionSyntax memberAccess)
            return IsExpressionFromTypeParameterArrayReturn(memberAccess.Name, context, requireActualTypeParam);

        // For invocation expressions, check the method being called
        if (expr is InvocationExpressionSyntax invocation)
        {
            var invSymbol = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (invSymbol != null && IsMethodReturningConvertedTypeParamArray(invSymbol, requireActualTypeParam))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a method's original return type is T[] (type parameter array) and the method
    /// is from a converted (source) type, not a framework type.
    /// </summary>
    private static bool IsMethodReturningConvertedTypeParamArray(IMethodSymbol method, bool requireActualTypeParam)
    {
        var origReturn = method.OriginalDefinition?.ReturnType;
        if (origReturn is not IArrayTypeSymbol origArray
            || origArray.Rank != 1
            || origArray.ElementType.TypeKind != TypeKind.TypeParameter)
            return false;

        // Framework methods (System.*) are NOT converted — they return real T[] arrays
        var ns = method.ContainingType?.ContainingNamespace?.ToDisplayString();
        if (ns != null && (ns.StartsWith("System", StringComparison.Ordinal) || ns == "System"))
            return false;

        if (!requireActualTypeParam)
            return true;

        // For requireActualTypeParam: check the actual return type.
        // Primitive element types (int, long, etc.) stay as real Java arrays.
        // Reference types and type parameters are converted to List<T>.
        var actualReturn = method.ReturnType;
        if (actualReturn is IArrayTypeSymbol actualArr)
        {
            if (IsJavaPrimitiveType(actualArr.ElementType))
                return false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a property's original type is T[] (type parameter array) and the property
    /// is from a converted (source) type, not a framework type.
    /// </summary>
    private static bool IsPropertyReturningConvertedTypeParamArray(IPropertySymbol prop, bool requireActualTypeParam)
    {
        var origReturn = prop.OriginalDefinition?.Type;
        if (origReturn is not IArrayTypeSymbol origArray
            || origArray.Rank != 1
            || origArray.ElementType.TypeKind != TypeKind.TypeParameter)
            return false;

        var ns = prop.ContainingType?.ContainingNamespace?.ToDisplayString();
        if (ns != null && (ns.StartsWith("System", StringComparison.Ordinal) || ns == "System"))
            return false;

        if (!requireActualTypeParam)
            return true;

        var actualType = prop.Type;
        if (actualType is IArrayTypeSymbol actualArr)
        {
            if (IsJavaPrimitiveType(actualArr.ElementType))
                return false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the given C# type maps to a Java primitive type.
    /// Such types cannot be used as generic type arguments (no List&lt;int&gt; in Java).
    /// </summary>
    private static bool IsJavaPrimitiveType(ITypeSymbol type)
    {
        return type.SpecialType is
            SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16 or
            SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_UInt16 or
            SpecialType.System_Double or SpecialType.System_Single or
            SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_Char;
    }
}
