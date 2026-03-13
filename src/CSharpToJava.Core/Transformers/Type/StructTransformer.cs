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

        // Propagate generic type parameter constraints
        ClassTransformer.ApplyTypeParameterConstraints(structDecl.ConstraintClauses, javaClass.TypeParameters, context);

        // 处理接口实现
        if (structDecl.BaseList != null)
        {
            foreach (var baseType in structDecl.BaseList.Types)
            {
                var typeInfo = context.SemanticModel?.GetTypeInfo(baseType.Type);
                if (typeInfo.HasValue && typeInfo.Value.Type?.TypeKind == TypeKind.Interface)
                {
                    var iface = typeInfo.Value.Type;
                    // Skip MarshalByRefObject - it doesn't exist in Java
                    if (iface.Name != "MarshalByRefObject" && iface.ToDisplayString() != "System.MarshalByRefObject")
                    {
                        javaClass.ImplementedTypes.Add(context.MapType(iface));
                    }
                }
            }
        }

        // 处理成员
        context.EnterType(javaClass);
        foreach (var member in structDecl.Members)
        {
            ProcessStructMember(member, javaClass, context);
        }
        context.LeaveType();

        // C# structs have implicit zero-arg constructors; add one to Java class if not already present.
        bool hasNoArgCtor = javaClass.Constructors.Any(c => c.Parameters.Count == 0);
        if (!hasNoArgCtor)
        {
            var defaultCtor = new JavaConstructorDeclaration
            {
                ClassName = javaClass.Name,
                Modifiers = JavaModifiers.Public,
                Body = ""
            };
            javaClass.Constructors.Insert(0, defaultCtor);
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

    private void ProcessStructMember(MemberDeclarationSyntax member, JavaClassDeclaration javaClass, ConversionContext context)
    {
        var factory = new Transformers.TransformerFactory();

        switch (member)
        {
            case FieldDeclarationSyntax fieldDecl:
                var fieldTransformer = new Transformers.Member.FieldTransformer();
                foreach (var javaField in fieldTransformer.TransformAll(fieldDecl, context))
                {
                    // struct 字段默认是 public（但不加 final，因为 struct 的属性可能有 setter）
                    if (javaField.Modifiers == JavaModifiers.None)
                    {
                        javaField.Modifiers = JavaModifiers.Public;
                    }
                    javaClass.Fields.Add(javaField);
                }
                break;

            case PropertyDeclarationSyntax propDecl:
                // Skip explicit interface implementations - Java doesn't need them since the
                // public member already satisfies the interface requirement
                if (propDecl.ExplicitInterfaceSpecifier != null) break;
                var propTransformer = factory.CreatePropertyTransformer();
                var props = propTransformer.Transform(propDecl, context);
                if (props is JavaMemberCollection collection)
                {
                    foreach (var prop in collection.Members)
                    {
                        if (prop is JavaFieldDeclaration jf) javaClass.Fields.Add(jf);
                        if (prop is JavaMethodDeclaration jm) ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, jm);
                    }
                }
                else if (props is JavaFieldDeclaration jf)
                {
                    javaClass.Fields.Add(jf);
                }
                else if (props is JavaMethodDeclaration jm)
                {
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, jm);
                }
                break;

            case MethodDeclarationSyntax methodDecl:
                // Explicit interface implementations are generated as public methods (not skipped)
                var methodTransformer = factory.CreateMethodTransformer();
                var method = methodTransformer.Transform(methodDecl, context);
                if (method is JavaMethodDeclaration javaMethod)
                {
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, javaMethod);
                }
                break;

            case OperatorDeclarationSyntax opDecl:
                var opTransformer = new Transformers.Member.OperatorTransformer();
                var opMethod = opTransformer.Transform(opDecl, context);
                if (opMethod != null)
                    ClassTransformer.AddMethodIfNotDuplicateInternal(javaClass, opMethod);
                break;

            case ConstructorDeclarationSyntax ctorDecl:
                var ctorTransformer = factory.CreateConstructorTransformer();
                var ctor = ctorTransformer.Transform(ctorDecl, context);
                if (ctor is JavaConstructorDeclaration javaCtor)
                {
                    ClassTransformer.AddCtorIfNotDuplicateInternal(javaClass, javaCtor);
                }
                break;
        }
    }
}
