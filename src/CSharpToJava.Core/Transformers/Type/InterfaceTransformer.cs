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
                if (typeInfo.HasValue && typeInfo.Value.Type != null)
                {
                    javaInterface.ExtendedTypes.Add(context.MapType(typeInfo.Value.Type));
                }
            }
        }

        // 处理类型参数
        foreach (var typeParam in interfaceDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaInterface.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // Propagate generic type parameter constraints
        ClassTransformer.ApplyTypeParameterConstraints(interfaceDecl.ConstraintClauses, javaInterface.TypeParameters, context);

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
                if (props is JavaMemberCollection propCollection)
                {
                    foreach (var item in propCollection.Members)
                    {
                        // Only add method declarations (getters/setters) — skip backing fields
                        if (item is JavaMethodDeclaration jm)
                        {
                            // Interface methods must be abstract (no body)
                            jm.Body = null;
                            jm.IsBodyExpression = false;
                            javaInterface.Methods.Add(jm);
                        }
                    }
                }
                else if (props is JavaMethodDeclaration jm)
                {
                    jm.Body = null;
                    jm.IsBodyExpression = false;
                    javaInterface.Methods.Add(jm);
                }
                break;

            case IndexerDeclarationSyntax indexerDecl:
                var indexerTransformer = factory.CreateIndexerTransformer();
                var indexerResult = indexerTransformer.Transform(indexerDecl, context);
                if (indexerResult is JavaMemberCollection indexerCollection)
                {
                    foreach (var item in indexerCollection.Members)
                    {
                        if (item is JavaMethodDeclaration jm)
                        {
                            // Interface methods must be abstract (no body)
                            jm.Body = null;
                            jm.IsBodyExpression = false;
                            javaInterface.Methods.Add(jm);
                        }
                    }
                }
                break;
        }
    }
}
