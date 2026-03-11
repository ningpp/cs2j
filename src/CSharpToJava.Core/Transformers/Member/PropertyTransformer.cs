using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// 属性转换器 - 将 C# 属性转换为 Java 字段和 getter/setter 方法
/// </summary>
public class PropertyTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext context)
    {
        if (node is not PropertyDeclarationSyntax propDecl)
        {
            throw new ArgumentException($"Expected PropertyDeclarationSyntax, got {node.GetType()}");
        }

        var results = new List<JavaMemberDeclaration>();
        var propType = context.MapType(context.SemanticModel!.GetTypeInfo(propDecl.Type).Type!);
        var propName = propDecl.Identifier.Text;
        var fieldName = ToCamelCase(propName);
        var isStatic = propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        var modifiers = ConvertModifiers(propDecl.Modifiers, isStatic);

        // 判断是否有显式实现
        var hasGetter = propDecl.AccessorList != null &&
                        propDecl.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var hasSetter = propDecl.AccessorList != null &&
                        propDecl.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));

        var getAccessor = propDecl.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setAccessor = propDecl.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));

        // 处理自动属性 vs 显式属性
        var isAutoProperty = (getAccessor?.Body == null && getAccessor?.ExpressionBody == null) ||
                             (setAccessor?.Body == null && setAccessor?.ExpressionBody == null);

        // 检查是否是只读属性（只有 getter）
        var isReadOnly = hasGetter && !hasSetter;
        // 检查是否是只写属性（只有 setter）
        var isWriteOnly = hasSetter && !hasGetter;

        // Java 字段修饰符
        var fieldModifiers = modifiers;
        if (isReadOnly || (!isWriteOnly && !isStatic))
        {
            fieldModifiers |= JavaModifiers.Private;
        }

        // 创建后备字段
        var field = new JavaFieldDeclaration
        {
            Name = fieldName,
            Type = propType,
            Modifiers = fieldModifiers
        };

        // 如果有默认值
        if (propDecl.Initializer != null)
        {
            var exprTransformer = new Transformers.ExpressionTransformer();
            field.Initializer = exprTransformer.Transform(propDecl.Initializer.Value, context);
        }

        results.Add(field);

        // 创建 getter
        if (hasGetter || propDecl.AccessorList == null)  // 默认有 getter
        {
            var getterModifiers = modifiers & ~JavaModifiers.Private;
            if ((propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))))
            {
                getterModifiers = JavaModifiers.Private;
            }
            else
            {
                getterModifiers |= JavaModifiers.Public;
            }

            var getter = new JavaMethodDeclaration
            {
                Name = "get" + ToPascalCase(propName),
                ReturnType = propType,
                Modifiers = getterModifiers,
                Body = getAccessor?.ExpressionBody != null
                    ? new Transformers.ExpressionTransformer().Transform(getAccessor.ExpressionBody.Expression, context)
                    : $"return {fieldName};",
                IsBodyExpression = getAccessor?.ExpressionBody != null
            };

            // 处理显式 getter 主体
            if (getAccessor?.Body != null)
            {
                var statementTransformer = new Transformers.StatementTransformer();
                getter.Body = statementTransformer.TransformBlock(getAccessor.Body, context);
                getter.IsBodyExpression = false;
            }

            results.Add(getter);
        }

        // 创建 setter
        if (hasSetter)
        {
            var setterModifiers = modifiers & ~JavaModifiers.Private;
            if (propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)))
            {
                setterModifiers = JavaModifiers.Private;
            }
            else
            {
                setterModifiers |= JavaModifiers.Public;
            }

            var setter = new JavaMethodDeclaration
            {
                Name = "set" + ToPascalCase(propName),
                ReturnType = "void",
                Modifiers = setterModifiers,
                Parameters = { new JavaParameter(propType, "value") },
                Body = setAccessor?.ExpressionBody != null
                    ? new Transformers.ExpressionTransformer().Transform(setAccessor.ExpressionBody.Expression, context)
                    : $"this.{fieldName} = value;",
                IsBodyExpression = setAccessor?.ExpressionBody != null
            };

            // 处理显式 setter 主体
            if (setAccessor?.Body != null)
            {
                var statementTransformer = new Transformers.StatementTransformer();
                setter.Body = statementTransformer.TransformBlock(setAccessor.Body, context);
                setter.IsBodyExpression = false;
            }

            results.Add(setter);
        }

        return results;
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers, bool isStatic)
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
                SyntaxKind.VirtualKeyword => JavaModifiers.None,  // Java 默认 virtual
                SyntaxKind.OverrideKeyword => JavaModifiers.Override,
                SyntaxKind.NewKeyword => JavaModifiers.Override,
                SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                SyntaxKind.SealedKeyword => JavaModifiers.Final,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        if (isStatic)
        {
            result |= JavaModifiers.Static;
        }

        return result;
    }

    private string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLower(name[0]) + name.Substring(1);
    }

    private string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpper(name[0]) + name.Substring(1);
    }
}
