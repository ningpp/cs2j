using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
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
        var typeInfo = context.GetTypeInfo(propDecl.Type);
        var propType = typeInfo.Type != null
            ? context.MapType(typeInfo.Type)
            : context.MapTypeFromSyntax(propDecl.Type);
        var propName = ConversionContext.EscapeJavaKeyword(propDecl.Identifier.Text);
        var fieldName = ConversionContext.EscapeJavaKeyword(ToCamelCase(propName));
        var isStatic = propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        var modifiers = ConvertModifiers(propDecl.Modifiers, isStatic);
        var previousStaticContext = context.IsInStaticMember;
        if (isStatic)
            context.IsInStaticMember = true;
        var propertySymbol = context.GetDeclaredSymbol(propDecl);
        var propertyComments = context.GetDeclarationComments(propDecl, propertySymbol).ToCombinedComment();

        // 判断是否有显式实现
        var hasGetter = propDecl.AccessorList != null &&
                        propDecl.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var hasSetter = propDecl.AccessorList != null &&
                        propDecl.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

        var getAccessor = propDecl.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setAccessor = propDecl.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

        // 处理自动属性 vs 显式属性
        // Expression-bodied properties (e.g. public int X => expr;) are NOT auto-properties —
        // they have no backing field and should only produce a getter method.
        var isExpressionBodiedProperty = propDecl.ExpressionBody != null;
        var isAutoProperty = !isExpressionBodiedProperty
                          && getAccessor?.Body == null
                          && getAccessor?.ExpressionBody == null
                          && (setAccessor == null
                              || (setAccessor.Body == null && setAccessor.ExpressionBody == null));

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
        var isAbstract = propDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        var needsBackingField = (isAutoProperty || propDecl.Initializer != null)
                             && !isAbstract
                             && !context.IsInInterfaceBody;

        var field = new JavaFieldDeclaration
        {
            Name = fieldName,
            Type = propType,
            Modifiers = fieldModifiers
        };

        // C# structs are value types that can never be null — initialize backing fields
        // with default instances so Java code doesn't encounter null struct references.
        if (propDecl.Initializer == null && needsBackingField
            && StructCloneHelper.IsUserDefinedStruct(typeInfo.Type))
        {
            field.Initializer = $"new {propType}()";
        }

        // 如果有默认值
        if (propDecl.Initializer != null)
        {
            field.Initializer = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(propDecl.Initializer.Value, context);
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
                getterModifiers = JavaModifiers.Public | (modifiers & ~(JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private));
            }
            // If the accessor has an explicit access modifier, use that instead of the property-level one
            if (getAccessor?.Modifiers.Any() == true)
            {
                var accessorVisibility = ConvertModifiers(getAccessor.Modifiers, isStatic: false);
                getterModifiers = (getterModifiers & ~(JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private))
                                | (accessorVisibility & (JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private));
            }
            // 确保只有一个访问修饰符
            getterModifiers = GetSingleAccessModifier(getterModifiers);

            var getter = new JavaMethodDeclaration
            {
                Name = "get" + ToPascalCase(propName),
                ReturnType = propType,
                Modifiers = getterModifiers,
                LeadingComment = propertyComments,
                Body = isExpressionBodiedProperty
                    ? Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(propDecl.ExpressionBody!.Expression, context)
                    : getAccessor?.ExpressionBody != null
                        ? Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(getAccessor.ExpressionBody.Expression, context)
                        : (StructCloneHelper.IsUserDefinedStruct(typeInfo.Type)
                            ? $"return {fieldName}.clone();"
                            : $"return {fieldName};"),
                IsBodyExpression = isExpressionBodiedProperty || getAccessor?.ExpressionBody != null,
                IsAutoGenerated = true
            };

            // Expression-bodied property getters skip TransformReturnStatement, so
            // Stream/Array wrapping for Iterable/Collection/List return types must be done here.
            if (getter.IsBodyExpression && getter.Body != null)
            {
                var csExpr = isExpressionBodiedProperty
                    ? propDecl.ExpressionBody!.Expression
                    : getAccessor?.ExpressionBody?.Expression;
                if (csExpr != null)
                {
                    getter.Body = MethodTransformer.WrapExpressionBodyForIterableReturn(
                        getter.Body, csExpr, propDecl.Type, context);
                    getter.Body = StructCloneHelper.CloneStructValueIfNeeded(
                        csExpr,
                        getter.Body,
                        typeInfo.Type,
                        context);
                }
            }

            // 处理显式 getter 主体
            if (getAccessor?.Body != null)
            {
                var statementTransformer = new Transformers.Statement.StatementTransformer();
                // Detect yield-returning getter: convert to list accumulation pattern
                bool isYieldGetter = getAccessor.Body.DescendantNodes().OfType<YieldStatementSyntax>().Any();
                if (isYieldGetter)
                {
                    context.IsInYieldMethod = true;
                    context.AddImport("java.util.ArrayList");
                    context.AddImport("java.util.List");
                    var elemType = PropertyYieldExtractElementType(propType);
                    getter.ReturnType = $"List<{elemType ?? "Object"}>";
                    var body = statementTransformer.TransformBlock(getAccessor.Body, context);
                    var listType = elemType != null ? $"ArrayList<{elemType}>" : "ArrayList<Object>";
                    getter.Body = $"{listType} _yieldResult = new {listType}();\n        {body}\n        return _yieldResult;";
                    context.IsInYieldMethod = false;
                }
                else
                {
                    getter.StructuredBody = statementTransformer.TransformBlockToStructuredBody(getAccessor.Body, context);
                }
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
                setterModifiers = JavaModifiers.Public | (modifiers & ~(JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private));
            }
            // If the accessor has an explicit access modifier, use that instead of the property-level one
            if (setAccessor?.Modifiers.Any() == true)
            {
                var accessorVisibility = ConvertModifiers(setAccessor.Modifiers, isStatic: false);
                setterModifiers = (setterModifiers & ~(JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private))
                                | (accessorVisibility & (JavaModifiers.Public | JavaModifiers.Protected | JavaModifiers.Private));
            }
            // 确保只有一个访问修饰符
            setterModifiers = GetSingleAccessModifier(setterModifiers);

            var setter = new JavaMethodDeclaration
            {
                Name = "set" + ToPascalCase(propName),
                ReturnType = "void",
                Modifiers = setterModifiers,
                LeadingComment = hasGetter ? null : propertyComments,
                Parameters = { new JavaParameter(propType, "value") },
                Body = setAccessor?.ExpressionBody != null
                    ? Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(setAccessor.ExpressionBody.Expression, context)
                    : (StructCloneHelper.IsUserDefinedStruct(typeInfo.Type)
                        ? (isStatic ? $"{fieldName} = value.clone();" : $"this.{fieldName} = value.clone();")
                        : (isStatic ? $"{fieldName} = value;" : $"this.{fieldName} = value;")),
                IsBodyExpression = setAccessor?.ExpressionBody != null,
                IsAutoGenerated = true
            };

            // 处理显式 setter 主体
            if (setAccessor?.Body != null)
            {
                var statementTransformer = new Transformers.Statement.StatementTransformer();
                setter.StructuredBody = statementTransformer.TransformBlockToStructuredBody(setAccessor.Body, context);
                setter.IsBodyExpression = false;
            }

            // Detect C# 9 init-only setter and annotate the generated Java setter
            bool isInitSetter = setAccessor?.IsKind(SyntaxKind.InitAccessorDeclaration) == true;
            if (isInitSetter)
                setter.LeadingComment = ConvertedCommentSet.JoinComments(setter.LeadingComment, "/** @implNote Set during construction only (C# init accessor). */");

            results.Add(setter);
        }

        // 返回包装的结果
        context.IsInStaticMember = previousStaticContext;
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
        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    private static string? PropertyYieldExtractElementType(string javaType)
    {
        var m = System.Text.RegularExpressions.Regex.Match(javaType,
            @"^(?:Iterable|Iterator|List|ArrayList|Collection|IEnumerable)<(.+)>$");
        return m.Success ? m.Groups[1].Value : null;
    }
}
