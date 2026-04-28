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
        // Check for [Flags] attribute → generate as int-constants class instead of Java enum
        bool isFlags = enumDecl.AttributeLists.Any(al => al.Attributes.Any(a =>
            a.Name.ToString() is "Flags" or "FlagsAttribute" or "System.Flags" or "System.FlagsAttribute"));

        if (isFlags)
        {
            // For [Flags] enums, generate a Java class with public static final int constants.
            // This allows bitwise operations to work naturally as int operations.
            var flagsClass = new JavaClassDeclaration
            {
                Name = enumDecl.Identifier.Text,
                Modifiers = ConvertModifiers(enumDecl.Modifiers)
            };
            var flagsSymbol = context.SemanticModel?.GetDeclaredSymbol(enumDecl);
            flagsClass.LeadingComment = context.GetDeclarationComments(enumDecl, flagsSymbol).ToCombinedComment();

            // Register in context so that type references to this enum return "int"
            // Register both short name and fully-qualified name to cover cross-namespace references
            context.RegisterFlagsEnum(enumDecl.Identifier.Text);
            var fqn = string.IsNullOrEmpty(context.CurrentNamespace)
                ? enumDecl.Identifier.Text
                : $"{context.CurrentNamespace}.{enumDecl.Identifier.Text}";
            if (fqn != enumDecl.Identifier.Text)
                context.RegisterFlagsEnum(fqn);
            if (context.CurrentType is { Name: var containingTypeName })
            {
                var nestedFqn = string.IsNullOrEmpty(context.CurrentNamespace)
                    ? $"{containingTypeName}.{enumDecl.Identifier.Text}"
                    : $"{context.CurrentNamespace}.{containingTypeName}.{enumDecl.Identifier.Text}";
                context.RegisterFlagsEnum(nestedFqn);
            }

            // Determine if the underlying type is long/ulong → use "long" for constants and methods
            bool useLong = false;
            if (enumDecl.BaseList != null)
            {
                var baseTypeName = enumDecl.BaseList.Types.FirstOrDefault()?.Type.ToString() ?? "";
                useLong = baseTypeName is "long" or "ulong" or "System.Int64" or "System.UInt64";
            }
            string numType = useLong ? "long" : "int";

            // Determine declared values
            long nextValue = 0;
            foreach (var member in enumDecl.Members)
            {
                if (member is EnumMemberDeclarationSyntax enumMember)
                {
                    string fieldValue;
                    if (enumMember.EqualsValue != null)
                    {
                        var valueStr = enumMember.EqualsValue.Value.ToString().Trim();
                        // Preserve the original expression for the Java constant initializer
                        // (hex literals like 0x01 are valid Java; binary 0b... is valid in Java 7+
                        //  but we convert to decimal for clarity)
                        if (valueStr.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                        {
                            // Binary literal 0b... is valid in Java 7+ but we convert to decimal for clarity.
                            try
                            {
                                nextValue = Convert.ToInt64(valueStr.Substring(2), 2);
                                fieldValue = useLong ? nextValue.ToString() + "L" : nextValue.ToString();
                            }
                            catch { fieldValue = valueStr; }
                        }
                        else
                        {
                            // Transform complex expressions (e.g. int.MaxValue → Integer.MAX_VALUE)
                            fieldValue = TransformEnumValueExpression(enumMember.EqualsValue.Value);
                            // Append L suffix for long constants that don't already have it
                            if (useLong && !fieldValue.EndsWith("L", StringComparison.OrdinalIgnoreCase))
                                fieldValue += "L";
                            // Update nextValue for auto-increment tracking
                            if (valueStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                                long.TryParse(valueStr.Substring(2), NumberStyles.HexNumber, null, out nextValue);
                            else
                                long.TryParse(valueStr.TrimEnd('L', 'l'), out nextValue);
                        }
                    }
                    else
                    {
                        fieldValue = useLong ? nextValue.ToString() + "L" : nextValue.ToString();
                    }
                    flagsClass.Fields.Add(new JavaFieldDeclaration
                    {
                        Type = numType,
                        Name = enumMember.Identifier.Text,
                        Initializer = fieldValue,
                        Modifiers = JavaModifiers.Public | JavaModifiers.Static | JavaModifiers.Final
                    });
                    nextValue++;
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
            return flagsClass;
        }

        var javaEnum = new JavaEnumDeclaration
        {
            Name = enumDecl.Identifier.Text,
            Modifiers = ConvertModifiers(enumDecl.Modifiers)
        };
        var enumSymbol = context.SemanticModel?.GetDeclaredSymbol(enumDecl);
        javaEnum.LeadingComment = context.GetDeclarationComments(enumDecl, enumSymbol).ToCombinedComment();

        // Check if any member has an explicit value initializer
        bool hasExplicitValues = enumDecl.Members.OfType<EnumMemberDeclarationSyntax>()
            .Any(m => m.EqualsValue != null);

        if (hasExplicitValues)
        {
            // Java enums cannot have plain integer ordinals assigned at the call site.
            // Use a constructor-based value field pattern:
            //   enum Status { Open(10), Closed(20); private final int value; ... }
            long nextVal = 0;
            bool nextValValid = true; // false when we can't compute the next auto-increment value
            foreach (var member in enumDecl.Members)
            {
                if (member is EnumMemberDeclarationSyntax enumMember)
                {
                    string valStr;
                    if (enumMember.EqualsValue != null)
                    {
                        var rawValue = enumMember.EqualsValue.Value.ToString().Trim();
                        valStr = TransformEnumValueExpression(enumMember.EqualsValue.Value);
                        // Try to track the numeric value for auto-increment of subsequent members
                        if (rawValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            nextValValid = long.TryParse(rawValue.Substring(2), NumberStyles.HexNumber, null, out nextVal);
                        }
                        else
                        {
                            nextValValid = long.TryParse(valStr.TrimEnd('L', 'l'), out nextVal);
                        }
                    }
                    else
                    {
                        valStr = nextValValid ? nextVal.ToString() : nextVal.ToString();
                    }
                    javaEnum.Values.Add($"{enumMember.Identifier.Text}({valStr})");
                    if (nextValValid) nextVal++;
                }
            }

            // Register so that cast sites know to use getValue()/fromValue() instead of ordinal()/values()[]
            context.RegisterExplicitValueEnum(enumDecl.Identifier.Text);
            var fqn = string.IsNullOrEmpty(context.CurrentNamespace)
                ? enumDecl.Identifier.Text
                : $"{context.CurrentNamespace}.{enumDecl.Identifier.Text}";
            if (fqn != enumDecl.Identifier.Text)
                context.RegisterExplicitValueEnum(fqn);
            if (context.CurrentType is { Name: var containingTypeName })
            {
                var nestedFqn = string.IsNullOrEmpty(context.CurrentNamespace)
                    ? $"{containingTypeName}.{enumDecl.Identifier.Text}"
                    : $"{context.CurrentNamespace}.{containingTypeName}.{enumDecl.Identifier.Text}";
                context.RegisterExplicitValueEnum(nestedFqn);
            }

            // Add private final int value field
            javaEnum.Fields.Add(new JavaFieldDeclaration
            {
                Type = "int",
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
            ctor.Parameters.Add(new JavaParameter("int", "v"));
            javaEnum.Constructors.Add(ctor);

            // Add getValue() accessor
            javaEnum.Methods.Add(new JavaMethodDeclaration
            {
                ReturnType = "int",
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
                Body = $"for ({enumName} e : values()) {{ if (e.value == v) return e; }}\n        throw new IllegalArgumentException(\"No enum constant with value \" + v);"
            });
            javaEnum.Methods.Last().Parameters.Add(new JavaParameter("int", "v"));
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
                ("double" or "float", "MinValue") => $"(-{(primType.Keyword.Text == "double" ? "Double" : "Float")}.MAX_VALUE)",
                (_, "MaxValue")          => "MAX_VALUE",
                (_, "MinValue")          => "MIN_VALUE",
                (_, "Epsilon")           => "MIN_VALUE",
                (_, "PositiveInfinity")  => "POSITIVE_INFINITY",
                (_, "NegativeInfinity")  => "NEGATIVE_INFINITY",
                (_, "NaN")              => "NaN",
                _                        => member
            };
            return $"{boxed}.{mapped}";
        }
        return expr.ToString().Trim();
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
