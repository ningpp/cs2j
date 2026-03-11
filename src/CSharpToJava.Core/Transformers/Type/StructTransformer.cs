using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 结构体转换器 - 将 C# struct 转换为 Java 类
/// </summary>
public class StructTransformer : ITypeTransformer
{
    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        if (node is not StructDeclarationSyntax structDecl)
        {
            throw new ArgumentException($"Expected StructDeclarationSyntax, got {node.GetType()}");
        }

        // C# struct 转换为 Java 的 final 类
        var javaClass = new JavaClassDeclaration
        {
            Name = structDecl.Identifier.Text,
            Modifiers = ConvertModifiers(structDecl.Modifiers) | JavaModifiers.Final  // struct 是不可变的，使用 final
        };

        // 处理类型参数
        foreach (var typeParam in structDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaClass.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // 处理接口实现
        if (structDecl.BaseList != null)
        {
            foreach (var baseType in structDecl.BaseList.Types)
            {
                var typeInfo = context.SemanticModel?.GetTypeInfo(baseType.Type);
                if (typeInfo?.Type?.TypeKind == TypeKind.Interface)
                {
                    javaClass.ImplementedTypes.Add(context.MapType(typeInfo.Type));
                }
            }
        }

        // 处理成员
        foreach (var member in structDecl.Members)
        {
            ProcessStructMember(member, javaClass, context);
        }

        return javaClass;
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
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
                SyntaxKind.ReadOnlyKeyword => JavaModifiers.Final,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        return result;
    }

    private void ProcessStructMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass, ConversionContext context)
    {
        var factory = new Transformers.TransformerFactory();

        switch (member)
        {
            case FieldDeclarationSyntax fieldDecl:
                var fieldTransformer = factory.CreateFieldTransformer();
                var field = fieldTransformer.Transform(fieldDecl, context);
                if (field is JavaFieldDeclaration javaField)
                {
                    // struct 字段默认是 public，在 Java 中也应该是 public final
                    if (javaField.Modifiers == JavaModifiers.None)
                    {
                        javaField.Modifiers = JavaModifiers.Public | JavaModifiers.Final;
                    }
                    javaClass.Fields.Add(javaField);
                }
                break;

            case PropertyDeclarationSyntax propDecl:
                var propTransformer = factory.CreatePropertyTransformer();
                var props = propTransformer.Transform(propDecl, context);
                if (props is List<JavaMemberDeclaration> javaProps)
                {
                    foreach (var prop in javaProps)
                    {
                        if (prop is JavaFieldDeclaration jf) javaClass.Fields.Add(jf);
                        if (prop is JavaMethodDeclaration jm) javaClass.Methods.Add(jm);
                    }
                }
                break;

            case MethodDeclarationSyntax methodDecl:
                var methodTransformer = factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    javaClass.Methods.Add(javaMethod);
                }
                break;

            case ConstructorDeclarationSyntax ctorDecl:
                var ctorTransformer = factory.CreateConstructorTransformer();
                var ctor = ctorTransformer.Transform(ctorDecl, context);
                if (ctor is JavaConstructorDeclaration javaCtor)
                {
                    javaClass.Constructors.Add(javaCtor);
                }
                break;
        }
    }
}
