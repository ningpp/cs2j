using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// 字段转换器
/// </summary>
public class FieldTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext context)
    {
        if (node is not FieldDeclarationSyntax fieldDecl)
        {
            throw new ArgumentException($"Expected FieldDeclarationSyntax, got {node.GetType()}");
        }

        var typeInfo = context.SemanticModel?.GetTypeInfo(fieldDecl.Declaration.Type);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";

        // 每个变量声明可能有多个声明符
        // 处理第一个（主要的）声明符
        var firstVariable = fieldDecl.Declaration.Variables.FirstOrDefault();
        if (firstVariable == null)
        {
            throw new InvalidOperationException("Field declaration has no variables");
        }

        var javaField = new JavaFieldDeclaration
        {
            Type = javaType,
            Name = firstVariable.Identifier.Text,
            Modifiers = ConvertModifiers(fieldDecl.Modifiers)
        };

        // 处理初始化器
        if (firstVariable.Initializer != null)
        {
            var exprTransformer = new Transformers.Expression.ExpressionTransformer();
            javaField.Initializer = exprTransformer.Transform(firstVariable.Initializer.Value, context);
        }

        // 处理 const 字段
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
        {
            javaField.Modifiers |= JavaModifiers.Static | JavaModifiers.Final;
        }

        // 处理 readonly 字段
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
        {
            javaField.Modifiers |= JavaModifiers.Final;
        }

        // 处理 fixed 字段（固定大小缓冲区）
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.FixedKeyword)))
        {
            context.Diagnostics.Error(
                "Java doesn't support fixed-size buffers. Field needs manual conversion.",
                fieldDecl.GetLocation()
            );
        }

        return javaField;
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.StaticKeyword => JavaModifiers.Static,
                SyntaxKind.ReadOnlyKeyword => JavaModifiers.Final,
                SyntaxKind.ConstKeyword => JavaModifiers.Static | JavaModifiers.Final,
                SyntaxKind.VolatileKeyword => JavaModifiers.Volatile,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                SyntaxKind.NewKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        return result;
    }
}
