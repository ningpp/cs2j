using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

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
    /// Formats an enum member access using a single symbol-based rule shared across
    /// expression contexts and switch labels.
    /// </summary>
    public static bool TryFormatEnumMemberAccess(
        ExpressionSyntax expression,
        ConversionContext context,
        bool useUnqualifiedRegularEnumInSwitchLabel,
        out string formattedAccess)
    {
        formattedAccess = string.Empty;

        if (!TryGetEnumMemberSymbol(expression, context, out var enumMember))
            return false;

        if (IsFlagsEnum(enumMember.ContainingType, context))
        {
            formattedAccess = $"{GetJavaStaticTypeReference(enumMember.ContainingType, context, preserveEnumType: true)}.{enumMember.Name}";
            return true;
        }

        var enumTypeReference = GetJavaStaticTypeReference(enumMember.ContainingType, context, preserveEnumType: false);
        formattedAccess = useUnqualifiedRegularEnumInSwitchLabel
            ? enumMember.Name
            : $"{enumTypeReference}.{enumMember.Name}";
        return true;
    }

    /// <summary>
    /// Resolves a static receiver expression to the Roslyn type symbol when the receiver denotes a type.
    /// This covers aliases and relative namespace paths as well as fully-qualified type references.
    /// </summary>
    public static bool TryGetStaticReceiverType(
        ExpressionSyntax expression,
        ConversionContext context,
        out INamedTypeSymbol typeSymbol)
    {
        typeSymbol = null!;

        if (context.SemanticModel == null)
            return false;

        var symbolInfo = context.SemanticModel.GetSymbolInfo(expression);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

        switch (symbol)
        {
            case IAliasSymbol { Target: INamedTypeSymbol aliasedType }:
                typeSymbol = aliasedType;
                return true;

            case INamedTypeSymbol namedType when namedType.TypeKind != TypeKind.Error:
                typeSymbol = namedType;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks whether a static receiver expression matches any of the provided C# type names.
    /// Semantic symbol resolution is preferred and syntax text is used only as a fallback.
    /// </summary>
    public static bool StaticReceiverMatches(
        ExpressionSyntax expression,
        ConversionContext context,
        params string[] candidateTypeNames)
    {
        if (TryGetStaticReceiverType(expression, context, out var typeSymbol))
        {
            return candidateTypeNames.Any(candidate => StaticReceiverMatchesCandidate(typeSymbol, candidate));
        }

        var receiverText = expression.ToString();
        return candidateTypeNames.Any(candidate => string.Equals(receiverText, candidate, StringComparison.Ordinal));
    }

    /// <summary>
    /// Formats a static receiver expression as a Java type reference based on its semantic symbol.
    /// Generic arguments are stripped because Java forbids them at static call sites.
    /// </summary>
    public static bool TryGetStaticTypeReceiverJavaReference(
        ExpressionSyntax expression,
        ConversionContext context,
        bool boxJavaPrimitiveType,
        out string javaReceiver,
        out INamedTypeSymbol typeSymbol)
    {
        javaReceiver = string.Empty;
        typeSymbol = null!;

        if (!TryGetStaticReceiverType(expression, context, out typeSymbol))
            return false;

        javaReceiver = GetJavaStaticTypeReference(
            typeSymbol,
            context,
            preserveEnumType: typeSymbol.TypeKind == TypeKind.Enum);

        javaReceiver = StripTypeArguments(javaReceiver);
        if (boxJavaPrimitiveType)
            javaReceiver = BoxJavaPrimitiveType(javaReceiver);

        return true;
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

    private static bool TryGetEnumMemberSymbol(
        ExpressionSyntax expression,
        ConversionContext context,
        out IFieldSymbol enumMember)
    {
        enumMember = null!;

        if (context.SemanticModel == null)
            return false;

        var symbolInfo = context.SemanticModel.GetSymbolInfo(expression);
        var symbol = symbolInfo.Symbol as IFieldSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IFieldSymbol>().FirstOrDefault();

        if (symbol?.ContainingType?.TypeKind != TypeKind.Enum)
            return false;

        enumMember = symbol;
        return true;
    }

    private static bool IsFlagsEnum(INamedTypeSymbol enumType, ConversionContext context)
    {
        return context.IsFlagsEnum(enumType.Name)
            || context.IsFlagsEnum(enumType.ToDisplayString())
            || enumType.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() is "System.FlagsAttribute" or "System.Flags" or "FlagsAttribute" or "Flags");
    }

    private static bool StaticReceiverMatchesCandidate(INamedTypeSymbol typeSymbol, string candidateTypeName)
    {
        if (string.IsNullOrWhiteSpace(candidateTypeName))
            return false;

        if (MatchesSpecialTypeAlias(typeSymbol, candidateTypeName))
            return true;

        var displayName = typeSymbol.ToDisplayString();
        var fullyQualifiedName = NormalizeFullyQualifiedName(typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        return string.Equals(displayName, candidateTypeName, StringComparison.Ordinal)
            || string.Equals(fullyQualifiedName, candidateTypeName, StringComparison.Ordinal)
            || string.Equals(typeSymbol.Name, candidateTypeName, StringComparison.Ordinal);
    }

    private static bool MatchesSpecialTypeAlias(INamedTypeSymbol typeSymbol, string candidateTypeName)
    {
        return candidateTypeName switch
        {
            "bool" => typeSymbol.SpecialType == SpecialType.System_Boolean,
            "byte" => typeSymbol.SpecialType == SpecialType.System_Byte,
            "char" => typeSymbol.SpecialType == SpecialType.System_Char,
            "double" => typeSymbol.SpecialType == SpecialType.System_Double,
            "float" => typeSymbol.SpecialType == SpecialType.System_Single,
            "int" => typeSymbol.SpecialType == SpecialType.System_Int32,
            "long" => typeSymbol.SpecialType == SpecialType.System_Int64,
            "object" => typeSymbol.SpecialType == SpecialType.System_Object,
            "short" => typeSymbol.SpecialType == SpecialType.System_Int16,
            "string" => typeSymbol.SpecialType == SpecialType.System_String,
            _ => false,
        };
    }

    private static string GetJavaStaticTypeReference(
        INamedTypeSymbol typeSymbol,
        ConversionContext context,
        bool preserveEnumType)
    {
        if (!preserveEnumType)
        {
            var mappedType = context.MapType(typeSymbol);
            if (!string.IsNullOrWhiteSpace(mappedType))
                return BoxJavaPrimitiveType(mappedType);
        }

        AddImportForTopLevelType(typeSymbol, context);
        return BuildNestedTypeReference(typeSymbol);
    }

    private static void AddImportForTopLevelType(INamedTypeSymbol typeSymbol, ConversionContext context)
    {
        var topLevelType = typeSymbol;
        while (topLevelType.ContainingType is INamedTypeSymbol parentType)
            topLevelType = parentType;

        var namespaceName = topLevelType.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrWhiteSpace(namespaceName)
            || namespaceName == "<global namespace>"
            || string.Equals(namespaceName, context.CurrentNamespace, StringComparison.Ordinal))
        {
            return;
        }

        context.AddImport($"{context.NamespaceToPackage(namespaceName)}.{topLevelType.Name}");
    }

    private static string BuildNestedTypeReference(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.ContainingType is INamedTypeSymbol parentType
            ? $"{BuildNestedTypeReference(parentType)}.{typeSymbol.Name}"
            : typeSymbol.Name;
    }

    private static string NormalizeFullyQualifiedName(string name)
    {
        return name.StartsWith("global::", StringComparison.Ordinal)
            ? name.Substring(8)
            : name;
    }

    private static string StripTypeArguments(string typeName)
    {
        if (!typeName.Contains('<', StringComparison.Ordinal))
            return typeName;

        var builder = new StringBuilder(typeName.Length);
        var depth = 0;

        foreach (var ch in typeName)
        {
            if (ch == '<')
            {
                depth++;
                continue;
            }

            if (ch == '>')
            {
                depth--;
                continue;
            }

            if (depth == 0)
                builder.Append(ch);
        }

        return builder.ToString();
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
    /// Strips ".collect(Collectors.toCollection(() -> new ArrayList<>()))" or similar from a stream expression.
    /// </summary>
    public static string StripCollect(string target)
    {
        var patterns = new[]
        {
            ".collect(Collectors.toCollection(() -> new ArrayList<>()))",
            ".collect(Collectors.toSet())",
            ".collect(Collectors.toCollection(LinkedList::new))",
            ".collect(Collectors.toCollection(HashSet::new))",
            ".collect(Collectors.toCollection(LinkedHashSet::new))"
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
        return t.Contains(".collect(Collectors.toCollection(() -> new ArrayList<>()))") ||
               t.Contains(".collect(Collectors.toSet())") ||
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
    /// Returns true if Java's <c>Arrays.stream()</c> supports this element type.
    /// Java only has overloads for <c>int[]</c>, <c>long[]</c>, <c>double[]</c>, and <c>T[]</c> (reference).
    /// <c>short[]</c>, <c>byte[]</c>, <c>char[]</c>, <c>float[]</c>, <c>boolean[]</c> are NOT supported.
    /// C# structs map to Java classes, so their arrays are reference-type T[] in Java and ARE supported.
    /// </summary>
    public static bool IsArraysStreamSupported(IArrayTypeSymbol arrayType)
    {
        var st = arrayType.ElementType.SpecialType;
        if (!IsPrimitiveSpecialTypeForArrayStream(st))
            return true; // reference type, struct, or non-primitive → Java reference T[] always supported
        return st is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double;
    }

    /// <summary>
    /// Builds a Java stream expression for a C# array expression.
    /// Java <c>Arrays.stream()</c> only supports <c>int[]</c>, <c>long[]</c>, <c>double[]</c>, and <c>T[]</c>.
    /// For unsupported C# primitive arrays (<c>short[]</c>, <c>byte[]</c>, <c>char[]</c>, <c>float[]</c>,
    /// <c>boolean[]</c>), uses <c>IntStream.range</c>-based alternatives.
    /// C# structs map to Java classes; their arrays use <c>Arrays.stream()</c> like any reference type.
    /// </summary>
    /// <param name="expr">The Java expression for the array.</param>
    /// <param name="arrayType">The C# array type symbol.</param>
    /// <param name="context">The conversion context (for imports).</param>
    /// <param name="boxed">When true, produces a boxed <c>Stream&lt;T&gt;</c> (e.g. <c>Stream&lt;Integer&gt;</c>)
    /// instead of a primitive stream (e.g. <c>IntStream</c>).</param>
    public static string BuildArrayStreamExpression(
        string expr, IArrayTypeSymbol arrayType, ConversionContext context, bool boxed = false)
    {
        var elemSt = arrayType.ElementType.SpecialType;

        // Check if this is a C# primitive type that needs special handling
        if (!IsPrimitiveSpecialTypeForArrayStream(elemSt))
        {
            // Not a primitive (reference type, struct, enum, etc.) → Java reference T[] always works
            context.AddImport("java.util.Arrays");
            return $"Arrays.stream({expr})";
        }

        if (elemSt is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double)
        {
            // Supported primitive: Arrays.stream(expr) → IntStream/LongStream/DoubleStream
            context.AddImport("java.util.Arrays");
            return boxed ? $"Arrays.stream({expr}).boxed()" : $"Arrays.stream({expr})";
        }

        // Unsupported Java primitive: IntStream.range-based
        context.AddImport("java.util.stream.IntStream");
        if (boxed || elemSt == SpecialType.System_Boolean)
            return $"IntStream.range(0, {expr}.length).mapToObj(i -> {expr}[i])";
        if (elemSt == SpecialType.System_Single) // float → DoubleStream (widening)
            return $"IntStream.range(0, {expr}.length).mapToDouble(i -> {expr}[i])";
        // short, byte, char → IntStream (widening to int)
        return $"IntStream.range(0, {expr}.length).map(i -> {expr}[i])";
    }

    /// <summary>
    /// Builds a Java Collection expression for a C# array expression.
    /// For C# primitive arrays, boxes elements and collects to ArrayList.
    /// For reference-type or struct arrays, uses <c>Arrays.asList()</c>.
    /// </summary>
    public static string BuildArrayToCollectionExpression(
        string expr, IArrayTypeSymbol arrayType, ConversionContext context)
    {
        if (IsPrimitiveSpecialTypeForArrayStream(arrayType.ElementType.SpecialType))
        {
            context.AddImport("java.util.stream.Collectors");
            context.AddImport("java.util.ArrayList");
            var stream = BuildArrayStreamExpression(expr, arrayType, context, boxed: true);
            return $"{stream}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
        }

        context.AddImport("java.util.Arrays");
        return $"Arrays.asList({expr})";
    }

    /// <summary>
    /// Returns true if the given SpecialType is a C# primitive type that maps to a
    /// Java primitive type. These are the types where Arrays.stream() support matters:
    /// int, short, byte, long, double, float, boolean, char.
    /// C# structs (SpecialType.None) do NOT match — they map to Java reference types.
    /// </summary>
    private static bool IsPrimitiveSpecialTypeForArrayStream(SpecialType st)
        => st is SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
            or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Single
            or SpecialType.System_Boolean or SpecialType.System_Char;

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
            return BuildArrayStreamExpression(receiverExpr, arrayType, context, boxed: boxPrimitiveArrayElements);
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

    /// <summary>
    /// Checks whether a Java expression contains stream-like method calls at the outermost
    /// expression level (parenthesis depth 0), ignoring stream calls nested inside method
    /// arguments.  This avoids false positives where e.g.
    /// <c>createComponents(values().stream().toArray(...))</c> is incorrectly treated as a
    /// stream expression.
    /// </summary>
    public static bool ContainsStreamMethodAtTopLevel(string expr)
    {
        if (string.IsNullOrEmpty(expr)) return false;

        // Check top-level static factory calls (no leading dot)
        // These are always at depth 0 if they start the expression.
        int depth = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth < 0) depth = 0; }
            else if (c == '.' && depth == 0 && i + 1 < expr.Length)
            {
                var rest = expr.AsSpan(i + 1);
                if (StartsWithStreamMethod(rest))
                    return true;
            }
        }

        // Also check depth-0 top-level starts like Stream.concat(, StreamSupport.stream(, etc.
        if (expr.StartsWith("Stream.concat(", StringComparison.Ordinal) ||
            expr.StartsWith("StreamSupport.stream(", StringComparison.Ordinal) ||
            expr.StartsWith("Arrays.stream(", StringComparison.Ordinal) ||
            expr.StartsWith("IntStream.range(", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool StartsWithStreamMethod(ReadOnlySpan<char> s)
    {
        return s.StartsWith("stream(") || s.StartsWith("map(") || s.StartsWith("filter(") ||
               s.StartsWith("flatMap(") || s.StartsWith("sorted(") || s.StartsWith("distinct(") ||
               s.StartsWith("limit(") || s.StartsWith("skip(") || s.StartsWith("peek(") ||
               s.StartsWith("mapToInt(") || s.StartsWith("mapToLong(") || s.StartsWith("mapToDouble(") ||
               s.StartsWith("mapToObj(") || s.StartsWith("select(") || s.StartsWith("concat(");
    }
}
