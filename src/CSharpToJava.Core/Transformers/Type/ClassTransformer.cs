using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.PartialType;
using System.Text;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 类转换器 - 将 C# 类转换为 Java 类
/// </summary>
public class ClassTransformer : ITypeTransformer
{
    /// <summary>
    /// 转换已合并的 partial 类型声明
    /// </summary>
    public JavaTypeDeclaration TransformMerged(MergedTypeDeclaration mergedType, ConversionContext context)
    {
        if (mergedType.MergedSyntax is not ClassDeclarationSyntax classDecl)
        {
            throw new ArgumentException($"Expected merged ClassDeclarationSyntax, got {mergedType.MergedSyntax.GetType()}");
        }

        context.EnterType(CreatePlaceholderClass(classDecl.Identifier.Text));

        var javaClass = new JavaClassDeclaration
        {
            Name = mergedType.TypeSymbol.Name,
            Modifiers = ConvertModifiers(classDecl.Modifiers, context),
        };

        // Use the semantic model from the merged type for better type resolution
        var semanticModel = context.GetSemanticModelForTree(classDecl.SyntaxTree);

        // 处理基类 - 使用符号信息
        var baseType = mergedType.TypeSymbol.BaseType;
        if (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            javaClass.ExtendedType = context.MapType(baseType);
        }

        // 处理接口 - 使用符号信息
        foreach (var iface in mergedType.TypeSymbol.AllInterfaces)
        {
            // Only add directly implemented interfaces, not inherited ones
            var isDirect = false;
            foreach (var syntaxNode in mergedType.OriginalSyntaxNodes)
            {
                if (syntaxNode.BaseList != null)
                {
                    foreach (var baseTypeSyntax in syntaxNode.BaseList.Types)
                    {
                        var symbol = semanticModel?.GetSymbolInfo(baseTypeSyntax.Type).Symbol;
                        if (SymbolEqualityComparer.Default.Equals(symbol, iface))
                        {
                            isDirect = true;
                            break;
                        }
                    }
                }
            }

            if (isDirect)
            {
                javaClass.ImplementedTypes.Add(context.MapType(iface));
            }
        }

        // 处理类型参数 - 使用符号信息
        foreach (var typeParam in mergedType.TypeSymbol.TypeParameters)
        {
            javaClass.TypeParameters.Add(new JavaTypeParameter(typeParam.Name));
        }

        // 处理成员 - 使用合并后的语法节点
        foreach (var member in classDecl.Members)
        {
            ProcessMember(member, javaClass, context);
        }

        context.LeaveType();

        return javaClass;
    }

    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        // 检查这是否是已合并的 partial 类型的一部分
        if (context.IsMergedPartialType(node))
        {
            // 跳过处理 - 已合并的类型会通过 TransformMerged 处理
            // 返回一个占位符以避免处理错误
            context.EnterType(CreatePlaceholderClass(node is ClassDeclarationSyntax cls ? cls.Identifier.Text : "Unknown"));
            context.LeaveType();
            return new JavaClassDeclaration { Name = "MergedTypePlaceholder" };
        }

        if (node is not ClassDeclarationSyntax classDecl)
        {
            throw new ArgumentException($"Expected ClassDeclarationSyntax, got {node.GetType()}");
        }

        context.EnterType(CreatePlaceholderClass(classDecl.Identifier.Text));

        var javaClass = new JavaClassDeclaration
        {
            Name = GetJavaClassName(classDecl),
            Modifiers = ConvertModifiers(classDecl.Modifiers, context),
        };

        // 处理基类
        if (classDecl.BaseList != null)
        {
            foreach (var baseType in classDecl.BaseList.Types)
            {
                if (baseType.Type is SimpleNameSyntax simpleName)
                {
                    var typeName = simpleName.Identifier.Text;
                    if (typeName == "Object" || typeName == "ValueType") continue;

                    // 检查是否是基类（第一个通常是基类，后面是接口）
                    var typeInfo = context.SemanticModel?.GetTypeInfo(baseType.Type);
                    if (typeInfo?.Type?.TypeKind == TypeKind.Class)
                    {
                        javaClass.ExtendedType = context.MapType(typeInfo.Type);
                    }
                    else if (typeInfo?.Type?.TypeKind == TypeKind.Interface)
                    {
                        javaClass.ImplementedTypes.Add(context.MapType(typeInfo.Type));
                    }
                }
            }
        }

        // 处理类型参数
        foreach (var typeParam in classDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaClass.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // 处理成员
        foreach (var member in classDecl.Members)
        {
            ProcessMember(member, javaClass, context);
        }

        context.LeaveType();

        return javaClass;
    }

    private string GetJavaClassName(ClassDeclarationSyntax classDecl)
    {
        var name = classDecl.Identifier.Text;

        // 移除 C# 特殊后缀
        if (classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            // partial 类 - 保持原名称
        }

        return name;
    }

    private JavaClassDeclaration CreatePlaceholderClass(string name)
    {
        return new JavaClassDeclaration { Name = name };
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers, ConversionContext context)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public, // Java 没有 internal，使用 public
                SyntaxKind.StaticKeyword => JavaModifiers.Static,
                SyntaxKind.SealedKeyword => JavaModifiers.Final,
                SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                SyntaxKind.NewKeyword => JavaModifiers.Override, // new 成员
                SyntaxKind.OverrideKeyword => JavaModifiers.Override,
                SyntaxKind.VirtualKeyword => JavaModifiers.None, // Java 默认是 virtual
                SyntaxKind.UnsafeKeyword => JavaModifiers.None, // unsafe 不支持
                _ => JavaModifiers.None
            };
        }

        return result;
    }

    private void ProcessMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass, ConversionContext context)
    {
        var factory = new Transformers.TransformerFactory();

        switch (member)
        {
            case FieldDeclarationSyntax fieldDecl:
                var fieldTransformer = factory.CreateFieldTransformer();
                var field = fieldTransformer.Transform(fieldDecl, context);
                if (field is JavaFieldDeclaration javaField)
                {
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

            case IndexerDeclarationSyntax indexerDecl:
                var indexerTransformer = factory.CreateIndexerTransformer();
                var indexerMethods = indexerTransformer.Transform(indexerDecl, context);
                if (indexerMethods is List<JavaMethodDeclaration> methods)
                {
                    javaClass.Methods.AddRange(methods);
                }
                break;

            case ClassDeclarationSyntax nestedClass:
                var nestedTransformer = factory.CreateClassTransformer();
                var nestedClass = nestedTransformer.Transform(nestedClass, context);
                if (nestedClass is JavaClassDeclaration jc)
                {
                    javaClass.NestedTypes.Add(jc);
                }
                break;

            case InterfaceDeclarationSyntax nestedInterface:
                var interfaceTransformer = factory.CreateInterfaceTransformer();
                var nestedInterfaceDecl = interfaceTransformer.Transform(nestedInterface, context);
                if (nestedInterfaceDecl is JavaInterfaceDeclaration ji)
                {
                    javaClass.NestedTypes.Add(ji);
                }
                break;

            case EnumDeclarationSyntax nestedEnum:
                var enumTransformer = factory.CreateEnumTransformer();
                var nestedEnumDecl = enumTransformer.Transform(nestedEnum, context);
                if (nestedEnumDecl is JavaEnumDeclaration je)
                {
                    javaClass.NestedTypes.Add(je);
                }
                break;
        }
    }
}
