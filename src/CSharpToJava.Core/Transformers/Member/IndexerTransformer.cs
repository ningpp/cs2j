using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// 索引器转换器 - 将 C# 索引器转换为 Java 方法
/// </summary>
public class IndexerTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext context)
    {
        if (node is not IndexerDeclarationSyntax indexerDecl)
        {
            throw new ArgumentException($"Expected IndexerDeclarationSyntax, got {node.GetType()}");
        }

        var results = new List<JavaMethodDeclaration>();

        var typeInfo = context.SemanticModel?.GetTypeInfo(indexerDecl.Type);
        var returnType = typeInfo.HasValue && typeInfo.Value.Type != null
            ? context.MapType(typeInfo.Value.Type)
            : "Object";

        // 获取参数列表
        var parameters = new List<JavaParameter>();
        foreach (var param in indexerDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var paramTypeInfo = context.SemanticModel?.GetTypeInfo(param.Type!);
            var javaType = paramTypeInfo.HasValue && paramTypeInfo.Value.Type != null
                ? context.MapType(paramTypeInfo.Value.Type)
                : "Object";
            parameters.Add(new JavaParameter(javaType, param.Identifier.Text));
        }

        // 生成 getter 方法
        var getAccessor = indexerDecl.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));

        if (getAccessor != null || indexerDecl.AccessorList == null)
        {
            var getter = new JavaMethodDeclaration
            {
                Name = "get",
                ReturnType = returnType,
                Modifiers = GetAccessorModifiers(getAccessor, indexerDecl.Modifiers) | JavaModifiers.Public
            };

            // 添加参数
            foreach (var param in parameters)
            {
                getter.Parameters.Add(param);
            }

            if (getAccessor?.Body != null)
            {
                var statementTransformer = new Transformers.Statement.StatementTransformer();
                getter.Body = statementTransformer.TransformBlock(getAccessor.Body, context);
            }
            else if (getAccessor?.ExpressionBody != null)
            {
                var exprTransformer = new Transformers.Expression.ExpressionTransformer();
                getter.Body = exprTransformer.Transform(getAccessor.ExpressionBody.Expression, context);
                getter.IsBodyExpression = true;
            }

            results.Add(getter);
        }

        // 生成 setter 方法
        var setAccessor = indexerDecl.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));

        if (setAccessor != null)
        {
            var setter = new JavaMethodDeclaration
            {
                Name = "set",
                // Return the set value (returnType) rather than void, so that compound indexer
                // assignment (e.g. CdtEdge edge = Edges[i] = value) works in Java:
                // edge = Edges.set(i, value)  → returns value (the newly-set item).
                ReturnType = returnType,
                Modifiers = GetAccessorModifiers(setAccessor, indexerDecl.Modifiers) | JavaModifiers.Public
            };

            // 添加参数
            foreach (var param in parameters)
            {
                setter.Parameters.Add(param);
            }
            setter.Parameters.Add(new JavaParameter(returnType, "value"));

            if (setAccessor?.Body != null)
            {
                var statementTransformer = new Transformers.Statement.StatementTransformer();
                setter.Body = statementTransformer.TransformBlock(setAccessor.Body, context) + "\nreturn value;";
            }
            else if (setAccessor?.ExpressionBody != null)
            {
                var exprTransformer = new Transformers.Expression.ExpressionTransformer();
                setter.Body = exprTransformer.Transform(setAccessor.ExpressionBody.Expression, context);
                setter.IsBodyExpression = false; // need a block with return
                setter.Body += ";\nreturn value;";
            }

            results.Add(setter);
        }

        return new Java.JavaMemberCollection(results.Cast<Java.JavaSyntaxNode>().ToList());
    }

    private JavaModifiers GetAccessorModifiers(AccessorDeclarationSyntax? accessor, SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;

        // 检查访问器上的修饰符
        if (accessor != null)
        {
            foreach (var modifier in accessor.Modifiers)
            {
                result |= modifier.Kind() switch
                {
                    SyntaxKind.PublicKeyword => JavaModifiers.Public,
                    SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                    SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                    SyntaxKind.InternalKeyword => JavaModifiers.Public,
                    _ => JavaModifiers.None
                };
            }
        }

        // 如果访问器没有修饰符，使用索引器的修饰符
        if (result == JavaModifiers.None)
        {
            foreach (var modifier in modifiers)
            {
                result |= modifier.Kind() switch
                {
                    SyntaxKind.PublicKeyword => JavaModifiers.Public,
                    SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                    SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                    SyntaxKind.InternalKeyword => JavaModifiers.Public,
                    _ => JavaModifiers.None
                };
            }
        }

        return result;
    }
}
