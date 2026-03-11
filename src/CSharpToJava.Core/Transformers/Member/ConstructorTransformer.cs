using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// 构造函数转换器
/// </summary>
public class ConstructorTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext context)
    {
        if (node is not ConstructorDeclarationSyntax ctorDecl)
        {
            throw new ArgumentException($"Expected ConstructorDeclarationSyntax, got {node.GetType()}");
        }

        var className = context.CurrentType?.Name ?? ctorDecl.Identifier.Text;

        var javaCtor = new JavaConstructorDeclaration
        {
            ClassName = className,
            Modifiers = ConvertModifiers(ctorDecl.Modifiers)
        };

        // 处理参数
        foreach (var param in ctorDecl.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(param.Type!);
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";

            var javaParam = new JavaParameter(javaType, param.Identifier.Text);

            // 处理修饰符
            if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
            {
                // this 关键字在 Java 中用于构造函数链
                // 需要在方法体开头处理
            }
            if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
            {
                javaParam.IsVarArgs = true;
            }

            javaCtor.Parameters.Add(javaParam);
        }

        // 处理初始值设定项
        var initializerStatements = new List<string>();

        if (ctorDecl.Initializer != null)
        {
            if (ctorDecl.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword))
            {
                // this() 调用
                var args = GetInitializerArguments(ctorDecl.Initializer);
                initializerStatements.Add($"this({string.Join(", ", args)});");
            }
            else if (ctorDecl.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.BaseKeyword))
            {
                // base() 调用 - 在 Java 中是 super()
                var args = GetInitializerArguments(ctorDecl.Initializer);
                initializerStatements.Add($"super({string.Join(", ", args)});");
            }
        }

        // 处理构造函数体
        var bodyStatements = new List<string>();

        if (ctorDecl.Body != null)
        {
            var statementTransformer = new Transformers.Statement.StatementTransformer();
            bodyStatements.AddRange(statementTransformer.TransformStatements(ctorDecl.Body.Statements, context));
        }
        else if (ctorDecl.ExpressionBody != null)
        {
            var exprTransformer = new Transformers.Expression.ExpressionTransformer();
            bodyStatements.Add(exprTransformer.Transform(ctorDecl.ExpressionBody.Expression, context) + ";");
        }

        // 组合初始值设定项和主体
        if (initializerStatements.Count > 0 || bodyStatements.Count > 0)
        {
            var allStatements = initializerStatements.Concat(bodyStatements);
            javaCtor.Body = string.Join("\n        ", allStatements);
        }

        return javaCtor;
    }

    private List<string> GetInitializerArguments(ConstructorInitializerSyntax initializer)
    {
        var args = new List<string>();

        if (initializer.ArgumentList != null)
        {
            // 简化处理 - 实际应该转换表达式
            foreach (var arg in initializer.ArgumentList.Arguments)
            {
                // 这里应该使用表达式转换器
                args.Add(arg.ToString());
            }
        }

        return args;
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
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
                SyntaxKind.ExternKeyword => JavaModifiers.Native,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        return result;
    }
}
