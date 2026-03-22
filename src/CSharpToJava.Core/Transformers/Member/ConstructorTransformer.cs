using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;

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

        // Issue 1: Static constructors → Java static initializer block
        if (ctorDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
        {
            var staticBlock = new JavaStaticInitializerBlock();
            if (ctorDecl.Body != null)
            {
                var statementTransformer = new Transformers.Statement.StatementTransformer();
                staticBlock.Statements.AddRange(statementTransformer.TransformStatements(ctorDecl.Body.Statements, context));
            }
            return staticBlock;
        }

        var className = context.CurrentType?.Name ?? ctorDecl.Identifier.Text;

        var javaCtor = new JavaConstructorDeclaration
        {
            ClassName = className,
            Modifiers = ConvertModifiers(ctorDecl.Modifiers),
            LeadingComment = context.GetDeclarationComments(ctorDecl, context.SemanticModel?.GetDeclaredSymbol(ctorDecl)).ToCombinedComment()
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
            var args = GetInitializerArguments(ctorDecl.Initializer, context);

            // Issue 3: Only emit this()/super() when there are actual arguments.
            // Java implicitly calls super() with no args, so an empty super() call is redundant.
            if (args.Count > 0)
            {
                if (ctorDecl.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword))
                {
                    initializerStatements.Add($"this({string.Join(", ", args)});");
                }
                else if (ctorDecl.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.BaseKeyword))
                {
                    initializerStatements.Add($"super({string.Join(", ", args)});");
                }
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
            // Issue 4: Validate that the expression is void-compatible before emitting as a statement.
            var expr = ctorDecl.ExpressionBody.Expression;
            var exprTransformer = ExpressionTransformerFacade.Instance;
            var transformed = exprTransformer.Transform(expr, context);
            bool isVoidCompatible = expr is AssignmentExpressionSyntax
                || expr is InvocationExpressionSyntax
                || expr is PostfixUnaryExpressionSyntax
                || expr is PrefixUnaryExpressionSyntax;
            if (isVoidCompatible)
                bodyStatements.Add(transformed + ";");
            else
                bodyStatements.Add($"// TODO: verify expression body semantics: {transformed};");
        }

        // 组合初始值设定项和主体
        if (initializerStatements.Count > 0 || bodyStatements.Count > 0)
        {
            var allStatements = initializerStatements.Concat(bodyStatements);
            javaCtor.Body = string.Join("\n        ", allStatements);
        }
        else if (ctorDecl.Body != null)
        {
            // Empty block body (e.g., public Set() {}) → generate empty body, not abstract semicolon
            javaCtor.Body = "";
        }

        // Issue 5: For 'protected internal', annotate the body so readers know the access intent.
        if (IsProtectedInternal(ctorDecl.Modifiers))
        {
            var comment = "// C# 'protected internal' → Java 'protected' (package-private semantic is implicit via protected)";
            javaCtor.Body = javaCtor.Body == null
                ? comment
                : comment + "\n        " + javaCtor.Body;
        }

        return javaCtor;
    }

    private List<string> GetInitializerArguments(ConstructorInitializerSyntax initializer, ConversionContext context)
    {
        var args = new List<string>();

        if (initializer.ArgumentList != null)
        {
            var exprTransformer = ExpressionTransformerFacade.Instance;
            foreach (var arg in initializer.ArgumentList.Arguments)
            {
                // ArgumentSyntax 包含 Expression 属性
                args.Add(exprTransformer.Transform(arg.Expression, context));
            }
        }

        return args;
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;

        // Issue 5: Detect 'protected internal' before folding individual modifiers.
        // In Java the broadest equivalent is 'protected'; emit that with an informational comment.
        bool hasProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
        bool hasInternal  = modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        if (hasProtected && hasInternal)
        {
            // 'protected internal' → Java 'protected' (broader access, package-private is implicit)
            // Other modifiers (private, static, …) are still processed below.
            result |= JavaModifiers.Protected;
        }

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                // 'internal' maps to 'public' in Java, consistent with all other transformers.
                // Using package-private caused "not visible outside package" errors when the
                // enclosing class was already mapped to public.
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.StaticKeyword => JavaModifiers.Static,
                SyntaxKind.ExternKeyword => JavaModifiers.Native,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        return result;
    }

    private bool IsProtectedInternal(SyntaxTokenList modifiers)
    {
        return modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword))
            && modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
    }
}
