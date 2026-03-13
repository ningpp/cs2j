using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 记录 (record) 转换器
/// </summary>
public class RecordTransformer : ITypeTransformer
{
    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        if (node is not RecordDeclarationSyntax recordDecl)
        {
            throw new ArgumentException($"Expected RecordDeclarationSyntax, got {node.GetType()}");
        }

        // 如果目标 Java 版本支持 record (Java 14+)，使用 record
        var useRecord = context.Options.UseRecords &&
                       context.Options.TargetJavaVersion >= JavaVersion.Java17;

        if (useRecord)
        {
            return CreateJavaRecord(recordDecl, context);
        }
        else
        {
            // 否则创建等效的不可变类
            return CreateImmutableClass(recordDecl, context);
        }
    }

    private JavaTypeDeclaration CreateJavaRecord(RecordDeclarationSyntax recordDecl, ConversionContext context)
    {
        var javaRecord = new JavaClassDeclaration
        {
            Name = recordDecl.Identifier.Text,
            IsRecord = true,
            Modifiers = ConvertModifiers(recordDecl.Modifiers)
        };

        // 处理位置参数（record 的主要组件）
        foreach (var param in recordDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            // Java record 参数直接在声明中，不需要额外处理
            // 记录参数类型以便后续使用
            var typeInfo = context.SemanticModel?.GetTypeInfo(param.Type!);
            if (typeInfo.HasValue && typeInfo.Value.Type != null)
            {
                // 可以在这里记录类型信息
            }
        }

        // 处理基类/接口
        if (recordDecl.BaseList != null)
        {
            foreach (var baseType in recordDecl.BaseList.Types)
            {
                var typeInfo = context.SemanticModel?.GetTypeInfo(baseType.Type);
                if (typeInfo.HasValue && typeInfo.Value.Type?.TypeKind == TypeKind.Interface)
                {
                    javaRecord.ImplementedTypes.Add(context.MapType(typeInfo.Value.Type));
                }
            }
        }

        // 处理类型参数
        foreach (var typeParam in recordDecl.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            javaRecord.TypeParameters.Add(new JavaTypeParameter(typeParam.Identifier.Text));
        }

        // 处理附加成员
        foreach (var member in recordDecl.Members)
        {
            ProcessRecordMember(member, javaRecord, context);
        }

        return javaRecord;
    }

    private JavaTypeDeclaration CreateImmutableClass(RecordDeclarationSyntax recordDecl, ConversionContext context)
    {
        var javaClass = new JavaClassDeclaration
        {
            Name = recordDecl.Identifier.Text,
            Modifiers = ConvertModifiers(recordDecl.Modifiers) | JavaModifiers.Final
        };

        // 为每个位置参数创建字段和构造函数
        var fields = new List<JavaFieldDeclaration>();
        var constructorParams = new List<JavaParameter>();

        foreach (var param in recordDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(param.Type!);
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
            var paramName = param.Identifier.Text;

            // 创建 final 字段
            fields.Add(new JavaFieldDeclaration
            {
                Type = javaType,
                Name = ToCamelCase(paramName),
                Modifiers = JavaModifiers.Private | JavaModifiers.Final
            });

            constructorParams.Add(new JavaParameter(javaType, ToCamelCase(paramName)));
        }

        javaClass.Fields.AddRange(fields);

        // 创建构造函数
        if (constructorParams.Count > 0)
        {
            var ctor = new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = JavaModifiers.Public,
                Body = GenerateConstructorBody(fields.Select(f => f.Name).ToList())
            };
            // 添加参数到只读集合
            foreach (var param in constructorParams)
            {
                ctor.Parameters.Add(param);
            }
            javaClass.Constructors.Add(ctor);
        }

        // 为每个字段生成 getter
        foreach (var field in fields)
        {
            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Name = "get" + char.ToUpper(field.Name[0]) + field.Name.Substring(1),
                ReturnType = field.Type,
                Modifiers = JavaModifiers.Public,
                Body = $"return {field.Name};",
                IsBodyExpression = true
            });
        }

        // 生成 equals, hashCode, toString
        GenerateObjectMethods(javaClass);

        return javaClass;
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;
        bool hasAccessModifier = false;

        foreach (var modifier in modifiers)
        {
            // 使用 RawKind 来避免命名空间冲突
            var kind = (Microsoft.CodeAnalysis.CSharp.SyntaxKind)modifier.RawKind;
            result |= kind switch
            {
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword => JavaModifiers.Public,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InternalKeyword => JavaModifiers.Public,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SealedKeyword => JavaModifiers.Final,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
            if (kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword ||
                kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword ||
                kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.ProtectedKeyword ||
                kind == Microsoft.CodeAnalysis.CSharp.SyntaxKind.InternalKeyword)
                hasAccessModifier = true;
        }

        // Default to public when no explicit access modifier (C# default = internal)
        if (!hasAccessModifier)
            result |= JavaModifiers.Public;

        return result;
    }

    private string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLower(name[0]) + name.Substring(1);
    }

    private string GenerateConstructorBody(List<string> fieldNames)
    {
        var assignments = string.Join("\n        ", fieldNames.Select(name => $"this.{name} = {name};"));
        return assignments;
    }

    private void GenerateObjectMethods(JavaClassDeclaration javaClass)
    {
        // equals
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Name = "equals",
            ReturnType = "boolean",
            Modifiers = JavaModifiers.Public | JavaModifiers.Override,
            Parameters = { new JavaParameter("Object", "o") },
            Body = @"
        if (this == o) return true;
        if (o == null || getClass() != o.getClass()) return false;
        // TODO: Implement equals logic
        return false;"
        });

        // hashCode
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Name = "hashCode",
            ReturnType = "int",
            Modifiers = JavaModifiers.Public | JavaModifiers.Override,
            Body = @"
        // TODO: Implement hashCode logic
        return Objects.hash();",
            IsBodyExpression = false
        });

        // toString
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Name = "toString",
            ReturnType = "String",
            Modifiers = JavaModifiers.Public | JavaModifiers.Override,
            Body = @$"return ""{javaClass.Name}{{"" + ""; TODO: Implement toString logic }};"
        });
    }

    private void ProcessRecordMember(MemberDeclarationSyntax member, JavaClassDeclaration javaRecord, ConversionContext context)
    {
        // 处理 record 中的附加方法等
        var factory = new Transformers.TransformerFactory();

        switch (member)
        {
            case MethodDeclarationSyntax methodDecl:
                var methodTransformer = factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    javaRecord.Methods.Add(javaMethod);
                }
                break;

            case OperatorDeclarationSyntax opDecl:
                var opTransformer = new Transformers.Member.OperatorTransformer();
                var opMethod = opTransformer.Transform(opDecl, context);
                if (opMethod != null)
                    javaRecord.Methods.Add(opMethod);
                break;
        }
    }
}
