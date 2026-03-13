using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using System.Collections.Generic;

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

        var results = new List<JavaSyntaxNode>();
        var typeInfo = context.SemanticModel?.GetTypeInfo(propDecl.Type);
        var propType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        var propName = ConversionContext.EscapeJavaKeyword(propDecl.Identifier.Text);
        var fieldName = ConversionContext.EscapeJavaKeyword(ToCamelCase(propName));
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

        // Java 字段修饰符 - 字段应该是 private，除非是 static
        var fieldModifiers = JavaModifiers.Private;
        if (isStatic)
        {
            fieldModifiers |= JavaModifiers.Static;
        }

        // 创建后备字段 - 只有自动属性才需要后备字段
        // 显式属性（有body的getter/setter）直接在getter/setter中操作现有字段，不需要生成后备字段
        var hasExplicitGetterBody = getAccessor?.Body != null || getAccessor?.ExpressionBody != null;
        var hasExplicitSetterBody = setAccessor?.Body != null || setAccessor?.ExpressionBody != null;
        var needsBackingField = (hasGetter && !hasExplicitGetterBody) || (hasSetter && !hasExplicitSetterBody) ||
                                (propDecl.Initializer != null);

        var field = new JavaFieldDeclaration
        {
            Name = fieldName,
            Type = propType,
            Modifiers = fieldModifiers
        };

        // 如果有默认值
        if (propDecl.Initializer != null)
        {
            var exprTransformer = new Transformers.Expression.ExpressionTransformer();
            field.Initializer = exprTransformer.Transform(propDecl.Initializer.Value, context);
        }

        if (needsBackingField)
        {
            results.Add(field);
        }

        // 创建 getter
        if (hasGetter || propDecl.AccessorList == null)  // 默认有 getter
        {
            var getterModifiers = modifiers;
            // 如果属性本身没有访问修饰符，默认为 public
            if (!propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) ||
                                          m.IsKind(SyntaxKind.ProtectedKeyword) ||
                                          m.IsKind(SyntaxKind.PrivateKeyword) ||
                                          m.IsKind(SyntaxKind.InternalKeyword)))
            {
                getterModifiers = JavaModifiers.Public;
            }
            // 确保只有一个访问修饰符
            getterModifiers = GetSingleAccessModifier(getterModifiers);

            var getter = new JavaMethodDeclaration
            {
                Name = "get" + ToPascalCase(propName),
                ReturnType = propType,
                Modifiers = getterModifiers,
                Body = getAccessor?.ExpressionBody != null
                    ? new Transformers.Expression.ExpressionTransformer().Transform(getAccessor.ExpressionBody.Expression, context)
                    : $"return {fieldName};",
                IsBodyExpression = getAccessor?.ExpressionBody != null
            };

            // 处理显式 getter 主体
            if (getAccessor?.Body != null)
            {
                var statementTransformer = new Transformers.Statement.StatementTransformer();
                getter.Body = statementTransformer.TransformBlock(getAccessor.Body, context);
                getter.IsBodyExpression = false;
            }

            results.Add(getter);
        }

        // 创建 setter
        if (hasSetter)
        {
            var setterModifiers = modifiers;
            // 如果属性本身没有访问修饰符，默认为 public
            if (!propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) ||
                                          m.IsKind(SyntaxKind.ProtectedKeyword) ||
                                          m.IsKind(SyntaxKind.PrivateKeyword) ||
                                          m.IsKind(SyntaxKind.InternalKeyword)))
            {
                setterModifiers = JavaModifiers.Public;
            }
            // 确保只有一个访问修饰符
            setterModifiers = GetSingleAccessModifier(setterModifiers);

            var setter = new JavaMethodDeclaration
            {
                Name = "set" + ToPascalCase(propName),
                ReturnType = "void",
                Modifiers = setterModifiers,
                Parameters = { new JavaParameter(propType, "value") },
                Body = setAccessor?.ExpressionBody != null
                    ? new Transformers.Expression.ExpressionTransformer().Transform(setAccessor.ExpressionBody.Expression, context)
                    : $"this.{fieldName} = value;",
                IsBodyExpression = setAccessor?.ExpressionBody != null
            };

            // 处理显式 setter 主体
            if (setAccessor?.Body != null)
            {
                var statementTransformer = new Transformers.Statement.StatementTransformer();
                setter.Body = statementTransformer.TransformBlock(setAccessor.Body, context);
                setter.IsBodyExpression = false;
            }

            results.Add(setter);
        }

        // 返回包装的结果
        return new JavaMemberCollection(results);
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers, bool isStatic)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            // 使用 RawKind 而不是 Kind 属性来避免命名空间冲突
            var kind = (Microsoft.CodeAnalysis.CSharp.SyntaxKind)modifier.RawKind;
            result |= kind switch
            {
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword => JavaModifiers.Public,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InternalKeyword => JavaModifiers.Public,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword => JavaModifiers.Static,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.VirtualKeyword => JavaModifiers.None,  // Java 默认 virtual
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.OverrideKeyword => JavaModifiers.Override,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.NewKeyword => JavaModifiers.Override,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SealedKeyword => JavaModifiers.Final,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        if (isStatic)
        {
            result |= JavaModifiers.Static;
        }

        // C# "protected internal" maps to Protected | Public — Java doesn't allow both; keep Protected.
        if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
            result &= ~JavaModifiers.Public;

        return result;
    }

    private JavaModifiers GetSingleAccessModifier(JavaModifiers modifiers)
    {
        // 确保只有一个访问修饰符（public, protected, private）
        // 优先级：private > protected > public
        if ((modifiers & JavaModifiers.Private) != 0)
            return (modifiers & ~JavaModifiers.Public & ~JavaModifiers.Protected);
        if ((modifiers & JavaModifiers.Protected) != 0)
            return (modifiers & ~JavaModifiers.Public);
        return modifiers;
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
