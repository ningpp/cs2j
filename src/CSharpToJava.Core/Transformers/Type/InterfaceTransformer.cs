using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
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

        var interfaceSymbol = context.SemanticModel?.GetDeclaredSymbol(interfaceDecl);
        javaInterface.LeadingComment = context.GetDeclarationComments(interfaceDecl, interfaceSymbol).ToCombinedComment();

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

        // 处理成员 — Fix 4: create factory once outside the per-member loop
        var factory = new Transformers.TransformerFactory();
        foreach (var member in interfaceDecl.Members)
        {
            ProcessInterfaceMember(member, javaInterface, context, factory);
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

    private void ProcessInterfaceMember(MemberDeclarationSyntax member, JavaInterfaceDeclaration javaInterface, ConversionContext context, Transformers.TransformerFactory factory)
    {
        switch (member)
        {
            case MethodDeclarationSyntax methodDecl:
                var methodTransformer = factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    // Fix 1: C# 8 default interface method implementations → Java "default" keyword
                    bool hasBody = methodDecl.Body != null || methodDecl.ExpressionBody != null;
                    bool isStatic = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                    if (hasBody && !isStatic)
                    {
                        javaMethod.Modifiers |= JavaModifiers.Default;
                    }
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

            case EventFieldDeclarationSyntax eventFieldDecl:
                // Fix 6: Interface events — emit only abstract add/remove listener signatures;
                // no backing field and no fire method (those are class-level implementation details).
                var eventTransformer = factory.CreateEventFieldTransformer();
                var allEventMembers = eventTransformer.TransformEvent(eventFieldDecl, context);
                foreach (var em in allEventMembers)
                {
                    if (em is JavaMethodDeclaration evMethod &&
                        (evMethod.Name.StartsWith("add", StringComparison.Ordinal) ||
                         evMethod.Name.StartsWith("remove", StringComparison.Ordinal)))
                    {
                        evMethod.Body = null;
                        evMethod.IsBodyExpression = false;
                        javaInterface.Methods.Add(evMethod);
                    }
                }
                break;

            case ClassDeclarationSyntax nestedClass:
                // Fix 3: Nested type declarations inside interfaces
                var nestedClassTransformer = factory.CreateClassTransformer();
                var nestedClassResult = nestedClassTransformer.Transform(nestedClass, context);
                if (nestedClassResult is JavaClassDeclaration jc)
                {
                    jc.Modifiers |= JavaModifiers.Static;
                    javaInterface.NestedTypes.Add(jc);
                }
                break;

            case InterfaceDeclarationSyntax nestedInterface:
                var nestedInterfaceTransformer = factory.CreateInterfaceTransformer();
                var nestedInterfaceResult = nestedInterfaceTransformer.Transform(nestedInterface, context);
                if (nestedInterfaceResult is JavaInterfaceDeclaration ji)
                {
                    ji.Modifiers |= JavaModifiers.Static;
                    javaInterface.NestedTypes.Add(ji);
                }
                break;

            case EnumDeclarationSyntax nestedEnum:
                var enumTransformer = new EnumTransformer();
                var nestedEnumResult = enumTransformer.TransformEnum(nestedEnum, context);
                if (nestedEnumResult is JavaEnumDeclaration je)
                {
                    je.Modifiers |= JavaModifiers.Static;
                    javaInterface.NestedTypes.Add(je);
                }
                break;
        }
    }
}
