using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 接口转换器
/// </summary>
public class InterfaceTransformer : ITypeTransformer
{
    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        if (node is not InterfaceDeclarationSyntax interfaceDecl)
        {
            throw new ArgumentException($"Expected InterfaceDeclarationSyntax, got {node.GetType()}");
        }

        var javaInterface = new JavaInterfaceDeclaration
        {
            Name = interfaceDecl.Identifier.Text,
            Modifiers = ConvertModifiers(interfaceDecl.Modifiers)
        };

        // 处理基接口
        if (interfaceDecl.BaseList != null)
        {
            foreach (var baseType in interfaceDecl.BaseList.Types)
            {
                var typeInfo = context.SemanticModel?.GetTypeInfo(baseType.Type);
                if (typeInfo?.Type != null)
                {
                    javaInterface.ExtendedTypes.Add(context.MapType(typeInfo.Type));
                }
            }
        }

        // 处理类型参数
        foreach (var typeParam in interfaceDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaInterface.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // 处理成员
        foreach (var member in interfaceDecl.Members)
        {
            ProcessInterfaceMember(member, javaInterface, context);
        }

        return javaInterface;
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
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        return result;
    }

    private void ProcessInterfaceMember(MemberDeclarationSyntax member, JavaInterfaceDeclaration javaInterface, ConversionContext context)
    {
        var factory = new Transformers.TransformerFactory();

        switch (member)
        {
            case MethodDeclarationSyntax methodDecl:
                var methodTransformer = factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    javaInterface.Methods.Add(javaMethod);
                }
                break;

            case PropertyDeclarationSyntax propDecl:
                var propTransformer = factory.CreatePropertyTransformer();
                var props = propTransformer.Transform(propDecl, context);
                if (props is List<JavaMemberDeclaration> javaProps)
                {
                    foreach (var prop in javaProps)
                    {
                        if (prop is JavaMethodDeclaration jm) javaInterface.Methods.Add(jm);
                    }
                }
                break;

            case IndexerDeclarationSyntax indexerDecl:
                var indexerTransformer = factory.CreateIndexerTransformer();
                var indexerMethods = indexerTransformer.Transform(indexerDecl, context);
                if (indexerMethods is List<JavaMethodDeclaration> methods)
                {
                    javaInterface.Methods.AddRange(methods);
                }
                break;
        }
    }
}
