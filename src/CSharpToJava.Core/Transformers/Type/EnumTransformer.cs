using Microsoft.CodeAnalysis;
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
        if (node is not EnumDeclarationSyntax enumDecl)
        {
            throw new ArgumentException($"Expected EnumDeclarationSyntax, got {node.GetType()}");
        }

        var javaEnum = new JavaEnumDeclaration
        {
            Name = enumDecl.Identifier.Text
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
}
