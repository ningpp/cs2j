using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

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

            // Register in context so that type references to this enum return "int"
            context.RegisterFlagsEnum(enumDecl.Identifier.Text);

            // Determine declared values
            int nextValue = 0;
            foreach (var member in enumDecl.Members)
            {
                if (member is EnumMemberDeclarationSyntax enumMember)
                {
                    if (enumMember.EqualsValue != null)
                    {
                        // Parse simple integer constant
                        if (int.TryParse(enumMember.EqualsValue.Value.ToString(), out int parsedVal))
                            nextValue = parsedVal;
                    }
                    var fieldValue = nextValue.ToString();
                    flagsClass.Fields.Add(new JavaFieldDeclaration
                    {
                        Type = "int",
                        Name = enumMember.Identifier.Text,
                        Initializer = fieldValue,
                        Modifiers = JavaModifiers.Public | JavaModifiers.Static | JavaModifiers.Final
                    });
                    nextValue++;
                }
            }

            // Add static utility methods used by the converter for bitwise operations on this type
            void AddFlagsMethod(string name, string body, params JavaParameter[] args)
            {
                var m = new JavaMethodDeclaration
                {
                    ReturnType = "int", Name = name,
                    Modifiers = JavaModifiers.Public | JavaModifiers.Static,
                    Body = body
                };
                m.Parameters.AddRange(args);
                flagsClass.Methods.Add(m);
            }
            AddFlagsMethod("and",        "return a & b;", new JavaParameter("int", "a"), new JavaParameter("int", "b"));
            AddFlagsMethod("or",         "return a | b;", new JavaParameter("int", "a"), new JavaParameter("int", "b"));
            AddFlagsMethod("bitwiseOr",  "return a | b;", new JavaParameter("int", "a"), new JavaParameter("int", "b"));
            AddFlagsMethod("complement", "return ~a;",    new JavaParameter("int", "a"));
            return flagsClass;
        }

        var javaEnum = new JavaEnumDeclaration
        {
            Name = enumDecl.Identifier.Text,
            Modifiers = ConvertModifiers(enumDecl.Modifiers)
        };

        // 处理枚举成员
        foreach (var member in enumDecl.Members)
        {
            if (member is EnumMemberDeclarationSyntax enumMember)
            {
                var value = enumMember.Identifier.Text;

                // 处理枚举值初始化
                if (enumMember.EqualsValue != null)
                {
                    // C# 枚举可以有显式值，Java 枚举不支持
                    // 添加注释说明原始值
                    context.Diagnostics.Warning(
                        $"Java enum doesn't support explicit values. {value} originally had value: {enumMember.EqualsValue.Value}",
                        member.GetLocation()
                    );
                }

                javaEnum.Values.Add(value);
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
