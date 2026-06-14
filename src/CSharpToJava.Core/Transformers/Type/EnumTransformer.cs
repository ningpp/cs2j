using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using System.Globalization;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 枚举转换器
/// </summary>
public class EnumTransformer : ITypeTransformer
{
    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        // 由于 EnumDeclarationSyntax 不是 TypeDeclarationSyntax 的子类
        // 这个方法只会在类型推断匹配时被调用
        // 实际使用应该直接调用 TransformEnum
        throw new ArgumentException("Use TransformEnum method with EnumDeclarationSyntax");
    }

    public JavaTypeDeclaration TransformEnum(EnumDeclarationSyntax enumDecl, ConversionContext context)
    {
        var enumSymbol = context.GetDeclaredSymbol(enumDecl) as INamedTypeSymbol;
        var enumValueType = GetEnumValueJavaType(enumSymbol, enumDecl);

        // Check for [Flags] attribute → generate as int/long-constants class instead of Java enum
        bool isFlags = IsFlagsEnum(enumDecl, enumSymbol, context);

        if (isFlags)
        {
            // For [Flags] enums, generate a Java class with public static final int constants.
            // This allows bitwise operations to work naturally as int operations.
            var flagsClass = new JavaClassDeclaration
            {
                Name = enumDecl.Identifier.Text,
                Modifiers = ConvertModifiers(enumDecl.Modifiers)
            };
            flagsClass.LeadingComment = context.GetDeclarationComments(enumDecl, enumSymbol).ToCombinedComment();

            // Register in context so that type references to this enum return "int"
            // Register both short name and fully-qualified name to cover cross-namespace references
            RegisterEnumNames(enumDecl, enumSymbol, context, name => context.RegisterFlagsEnum(name, enumValueType));
            string numType = enumValueType;

            // Determine declared values
            foreach (var member in enumDecl.Members)
            {
                if (member is EnumMemberDeclarationSyntax enumMember)
                {
                    string fieldValue = FormatEnumMemberValue(enumMember, context, enumValueType);
                    flagsClass.Fields.Add(new JavaFieldDeclaration
                    {
                        Type = numType,
                        Name = enumMember.Identifier.Text,
                        Initializer = fieldValue,
                        Modifiers = JavaModifiers.Public | JavaModifiers.Static | JavaModifiers.Final
                    });
                }
            }

            // Add static utility methods used by the converter for bitwise operations on this type
            void AddFlagsMethod(string name, string returnType, string body, params JavaParameter[] args)
            {
                var m = new JavaMethodDeclaration
                {
                    ReturnType = returnType, Name = name,
                    Modifiers = JavaModifiers.Public | JavaModifiers.Static,
                    Body = body
                };
                m.Parameters.AddRange(args);
                flagsClass.Methods.Add(m);
            }
            AddFlagsMethod("and",        numType,   "return a & b;",               new JavaParameter(numType, "a"), new JavaParameter(numType, "b"));
            AddFlagsMethod("or",         numType,   "return a | b;",               new JavaParameter(numType, "a"), new JavaParameter(numType, "b"));
            AddFlagsMethod("complement", numType,   "return ~a;",                  new JavaParameter(numType, "a"));
            AddFlagsMethod("has",        "boolean", "return (flags & flag) != 0;", new JavaParameter(numType, "flags"), new JavaParameter(numType, "flag"));
            // fromValue and fromValueUnchecked for int→flags casts (identity)
            AddFlagsMethod("fromValue",          numType, "return v;", new JavaParameter("int", "v"));
            AddFlagsMethod("fromValueUnchecked", numType, "return v;", new JavaParameter("int", "v"));
            return flagsClass;
        }

        var javaEnum = new JavaEnumDeclaration
        {
            Name = enumDecl.Identifier.Text,
            Modifiers = ConvertModifiers(enumDecl.Modifiers)
        };
        javaEnum.LeadingComment = context.GetDeclarationComments(enumDecl, enumSymbol).ToCombinedComment();

        // Check if any member has an explicit value initializer
        bool hasExplicitValues = enumDecl.Members.OfType<EnumMemberDeclarationSyntax>()
            .Any(m => m.EqualsValue != null);

        if (hasExplicitValues)
        {
            // Java enums cannot have plain integer ordinals assigned at the call site.
            // Use a constructor-based value field pattern:
            //   enum Status { Open(10), Closed(20); private final int value; ... }
            foreach (var member in enumDecl.Members)
            {
                if (member is EnumMemberDeclarationSyntax enumMember)
                {
                    string valStr = FormatEnumMemberValue(enumMember, context, enumValueType);
                    javaEnum.Values.Add($"{enumMember.Identifier.Text}({valStr})");
                }
            }

            // Add _UNMAPPED sentinel for unnamed enum values (e.g., (UriFormat)0x7FFF)
            javaEnum.Values.Add($"_UNMAPPED(0)");

            // Register so that cast sites know to use getValue()/fromValue() instead of ordinal()/values()[]
            RegisterEnumNames(enumDecl, enumSymbol, context, name => context.RegisterExplicitValueEnum(name, enumValueType));

            // Add private final value field
            javaEnum.Fields.Add(new JavaFieldDeclaration
            {
                Type = enumValueType,
                Name = "value",
                Modifiers = JavaModifiers.Private | JavaModifiers.Final
            });

            // Add enum constructor (implicitly private in Java)
            var ctor = new JavaConstructorDeclaration
            {
                ClassName = javaEnum.Name,
                Modifiers = JavaModifiers.None,
                Body = "this.value = v;"
            };
            ctor.Parameters.Add(new JavaParameter(enumValueType, "v"));
            javaEnum.Constructors.Add(ctor);

            // Add getValue() accessor
            javaEnum.Methods.Add(new JavaMethodDeclaration
            {
                ReturnType = enumValueType,
                Name = "getValue",
                Modifiers = JavaModifiers.Public,
                Body = "return value;"
            });

            // Add fromValue() reverse lookup for int → enum casts
            var enumName = enumDecl.Identifier.Text;
            javaEnum.Methods.Add(new JavaMethodDeclaration
            {
                ReturnType = enumName,
                Name = "fromValue",
                Modifiers = JavaModifiers.Public | JavaModifiers.Static,
                Body = $"for ({enumName} e : values()) {{ if (e != _UNMAPPED && e.value == v) return e; }}\n        return _UNMAPPED;"
            });
            javaEnum.Methods.Last().Parameters.Add(new JavaParameter(enumValueType, "v"));

            // Add fromValueUnchecked() for unnamed enum value casts (e.g., (UriFormat)0x7FFF)
            javaEnum.Methods.Add(new JavaMethodDeclaration
            {
                ReturnType = enumName,
                Name = "fromValueUnchecked",
                Modifiers = JavaModifiers.Public | JavaModifiers.Static,
                Body = $"for ({enumName} e : values()) {{ if (e != _UNMAPPED && e.value == v) return e; }}\n        return _UNMAPPED;"
            });
            javaEnum.Methods.Last().Parameters.Add(new JavaParameter(enumValueType, "v"));
        }
        else
        {
            // 处理枚举成员
            foreach (var member in enumDecl.Members)
            {
                if (member is EnumMemberDeclarationSyntax enumMember)
                {
                    javaEnum.Values.Add(enumMember.Identifier.Text);
                }
            }

            // Always generate getValue() and fromValue() so that enum-to-int
            // conversions (e.g. (int)enumVal or 1 << enumVal) produce valid Java
            // code regardless of whether the enum has explicit value initializers.
            var enumName = enumDecl.Identifier.Text;
            javaEnum.Methods.Add(new JavaMethodDeclaration
            {
                ReturnType = enumValueType,
                Name = "getValue",
                Modifiers = JavaModifiers.Public,
                Body = "return ordinal();"
            });
            javaEnum.Methods.Add(new JavaMethodDeclaration
            {
                ReturnType = enumName,
                Name = "fromValue",
                Modifiers = JavaModifiers.Public | JavaModifiers.Static,
                Body = $"return values()[v];"
            });
            javaEnum.Methods.Last().Parameters.Add(new JavaParameter(enumValueType, "v"));
        }

        // 处理底层类型（C# 支持 byte, sbyte, short, ushort, int, uint, long, ulong）
        // Java 枚举底层固定是 int，无法更改
        if (enumDecl.BaseList != null)
        {
            context.Diagnostics.Warning(
                "Java enums always use int as underlying type",
                enumDecl.BaseList.GetLocation()
            );
        }

        return javaEnum;
    }

    /// <summary>
    /// Converts a C# enum member value expression to its Java equivalent.
    /// Handles primitive static constants (e.g. int.MaxValue → Integer.MAX_VALUE).
    /// Falls back to the raw syntax string for simple numeric literals.
    /// </summary>
    private static string TransformEnumValueExpression(ExpressionSyntax expr)
    {
        if (expr is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Expression is PredefinedTypeSyntax primType)
        {
            var boxed = ExpressionTransformerHelpers.BoxedTypeName(primType);
            var member = memberAccess.Name.Identifier.Text;
            var mapped = (primType.Keyword.Text, member) switch
            {
                ("byte", "MaxValue") => "255",
                ("byte", "MinValue") => "0",
                ("uint", "MaxValue") => "4294967295L",
                ("uint", "MinValue") => "0",
                ("ushort", "MaxValue") => "65535",
                ("ushort", "MinValue") => "0",
                ("ulong", "MaxValue") => "0xFFFFFFFFFFFFFFFFL",
                ("ulong", "MinValue") => "0",
                ("double" or "float", "MinValue") => $"(-{(primType.Keyword.Text == "double" ? "Double" : "Float")}.MAX_VALUE)",
                (_, "MaxValue")          => "MAX_VALUE",
                (_, "MinValue")          => "MIN_VALUE",
                (_, "Epsilon")           => "MIN_VALUE",
                (_, "PositiveInfinity")  => "POSITIVE_INFINITY",
                (_, "NegativeInfinity")  => "NEGATIVE_INFINITY",
                (_, "NaN")              => "NaN",
                _                        => member
            };
            // Literal values (start with digit or '0x') or parenthesized expressions
            // should not be prefixed with the boxed type name.
            if (mapped.StartsWith("(") || mapped.StartsWith("-")
                || (mapped.Length > 0 && char.IsDigit(mapped[0])))
                return mapped;
            return $"{boxed}.{mapped}";
        }
        return expr.ToString().Trim();
    }

    private static bool IsFlagsEnum(
        EnumDeclarationSyntax enumDecl,
        INamedTypeSymbol? enumSymbol,
        ConversionContext context)
    {
        if (enumSymbol?.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() is "System.FlagsAttribute"
                or "System.Flags"
                or "FlagsAttribute"
                or "Flags") == true)
        {
            return true;
        }

        foreach (var attribute in enumDecl.AttributeLists.SelectMany(al => al.Attributes))
        {
            var attributeType = context.GetTypeInfo(attribute).Type;
            if (attributeType?.ToDisplayString() is "System.FlagsAttribute"
                or "System.Flags"
                or "FlagsAttribute"
                or "Flags")
            {
                return true;
            }

            var name = attribute.Name.ToString();
            if (name is "Flags" or "FlagsAttribute" or "System.Flags" or "System.FlagsAttribute")
                return true;
        }

        return false;
    }

    private static string GetEnumValueJavaType(INamedTypeSymbol? enumSymbol, EnumDeclarationSyntax enumDecl)
    {
        if (enumSymbol?.EnumUnderlyingType?.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64)
            return "long";

        if (enumDecl.BaseList != null)
        {
            var baseTypeName = enumDecl.BaseList.Types.FirstOrDefault()?.Type.ToString() ?? "";
            if (baseTypeName is "long" or "ulong" or "System.Int64" or "System.UInt64")
                return "long";
        }

        return "int";
    }

    private static void RegisterEnumNames(
        EnumDeclarationSyntax enumDecl,
        INamedTypeSymbol? enumSymbol,
        ConversionContext context,
        Action<string> register)
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            enumDecl.Identifier.Text
        };

        if (enumSymbol != null)
        {
            AddIfNotEmpty(enumSymbol.Name);
            AddIfNotEmpty(enumSymbol.ToDisplayString());
            AddIfNotEmpty(TrimGlobalPrefix(enumSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }

        var fqn = string.IsNullOrEmpty(context.CurrentNamespace)
            ? enumDecl.Identifier.Text
            : $"{context.CurrentNamespace}.{enumDecl.Identifier.Text}";
        AddIfNotEmpty(fqn);

        if (context.CurrentType is { Name: var containingTypeName })
        {
            var nestedFqn = string.IsNullOrEmpty(context.CurrentNamespace)
                ? $"{containingTypeName}.{enumDecl.Identifier.Text}"
                : $"{context.CurrentNamespace}.{containingTypeName}.{enumDecl.Identifier.Text}";
            AddIfNotEmpty(nestedFqn);
            AddIfNotEmpty($"{containingTypeName}.{enumDecl.Identifier.Text}");
        }

        foreach (var name in names)
            register(name);

        void AddIfNotEmpty(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
    }

    private static string TrimGlobalPrefix(string name)
        => name.StartsWith("global::", StringComparison.Ordinal) ? name["global::".Length..] : name;

    private static string FormatEnumMemberValue(
        EnumMemberDeclarationSyntax enumMember,
        ConversionContext context,
        string javaValueType)
    {
        var valueExpr = enumMember.EqualsValue?.Value;
        var enumFieldSymbol = context.GetDeclaredSymbol(enumMember) as IFieldSymbol;
        var constantValue = enumFieldSymbol?.HasConstantValue == true
            ? enumFieldSymbol.ConstantValue
            : null;

        if (valueExpr != null
            && IsSimpleNumericLiteralExpression(valueExpr)
            && TryTransformSimpleNumericLiteral(valueExpr, javaValueType, out var literalValue))
        {
            return literalValue;
        }

        if (valueExpr != null && TryTransformPrimitiveConstantExpression(valueExpr, javaValueType, out var primitiveConstant))
            return primitiveConstant;

        if (TryFormatConstantValue(constantValue, javaValueType, out var semanticValue))
            return semanticValue;

        return valueExpr != null
            ? EnsureLongSuffix(TransformEnumValueExpression(valueExpr), javaValueType)
            : (javaValueType == "long" ? "0L" : "0");
    }

    private static bool IsSimpleNumericLiteralExpression(ExpressionSyntax expr)
    {
        return expr switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression) => true,
            PrefixUnaryExpressionSyntax prefix
                when prefix.IsKind(SyntaxKind.UnaryMinusExpression) || prefix.IsKind(SyntaxKind.UnaryPlusExpression)
                => IsSimpleNumericLiteralExpression(prefix.Operand),
            _ => false
        };
    }

    private static bool TryTransformSimpleNumericLiteral(ExpressionSyntax expr, string javaValueType, out string result)
    {
        if (expr is PrefixUnaryExpressionSyntax prefix
            && (prefix.IsKind(SyntaxKind.UnaryMinusExpression) || prefix.IsKind(SyntaxKind.UnaryPlusExpression)))
        {
            if (TryTransformSimpleNumericLiteral(prefix.Operand, javaValueType, out var operand))
            {
                result = prefix.IsKind(SyntaxKind.UnaryMinusExpression)
                    ? "-" + operand.TrimStart('+', '-')
                    : operand.TrimStart('+');
                return true;
            }
        }

        if (expr is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression))
        {
            var raw = literal.Token.Text.Replace("_", "", StringComparison.Ordinal);
            var suffixless = raw.TrimEnd('u', 'U', 'l', 'L');

            if (suffixless.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                if (TryConvertUnsignedLiteral(suffixless, 2, out var binaryValue))
                {
                    result = FormatIntegralValue(binaryValue, javaValueType);
                    return true;
                }
            }

            result = EnsureLongSuffix(suffixless, javaValueType);
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool TryTransformPrimitiveConstantExpression(ExpressionSyntax expr, string javaValueType, out string result)
    {
        result = string.Empty;
        if (expr is not MemberAccessExpressionSyntax memberAccess ||
            memberAccess.Expression is not PredefinedTypeSyntax primType)
        {
            return false;
        }

        result = EnsureLongSuffix(TransformEnumValueExpression(expr), javaValueType);
        return true;
    }

    private static bool TryFormatConstantValue(object? constantValue, string javaValueType, out string result)
    {
        switch (constantValue)
        {
            case byte value:
                result = FormatIntegralValue(value, javaValueType);
                return true;
            case sbyte value:
                result = FormatIntegralValue(value, javaValueType);
                return true;
            case short value:
                result = FormatIntegralValue(value, javaValueType);
                return true;
            case ushort value:
                result = FormatIntegralValue(value, javaValueType);
                return true;
            case int value:
                result = FormatIntegralValue(value, javaValueType);
                return true;
            case uint value:
                result = FormatIntegralValue(value, javaValueType);
                return true;
            case long value:
                result = FormatIntegralValue(value, javaValueType);
                return true;
            case ulong value when value <= long.MaxValue:
                result = FormatIntegralValue((long)value, javaValueType);
                return true;
            case ulong value:
                result = javaValueType == "long"
                    ? unchecked((long)value).ToString(CultureInfo.InvariantCulture) + "L"
                    : unchecked((int)value).ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                result = string.Empty;
                return false;
        }
    }

    private static string FormatIntegralValue(long value, string javaValueType)
        => value.ToString(CultureInfo.InvariantCulture) + (javaValueType == "long" ? "L" : "");

    private static string FormatIntegralValue(ulong value, string javaValueType)
    {
        if (value <= long.MaxValue)
            return FormatIntegralValue((long)value, javaValueType);

        return javaValueType == "long"
            ? unchecked((long)value).ToString(CultureInfo.InvariantCulture) + "L"
            : unchecked((int)value).ToString(CultureInfo.InvariantCulture);
    }

    private static string EnsureLongSuffix(string value, string javaValueType)
    {
        if (javaValueType != "long")
            return value;

        var trimmed = value.Trim();
        if (trimmed.EndsWith("L", StringComparison.OrdinalIgnoreCase))
            return trimmed.TrimEnd('l') + "L";
        if (trimmed.EndsWith("u", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.TrimEnd('u', 'U');
        return trimmed + "L";
    }

    private static bool TryConvertUnsignedLiteral(string literalText, int fromBase, out ulong value)
    {
        try
        {
            var digits = literalText.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ||
                literalText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? literalText[2..]
                : literalText;
            value = Convert.ToUInt64(digits, fromBase);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;
        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                _ => JavaModifiers.None
            };
        }
        // Default to public when no explicit access modifier (C# default = internal)
        bool hasAccessModifier = modifiers.Any(m =>
            m.IsKind(SyntaxKind.PublicKeyword) ||
            m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) ||
            m.IsKind(SyntaxKind.InternalKeyword));
        if (!hasAccessModifier)
            result |= JavaModifiers.Public;
        return result;
    }
}
