using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Utilities;

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

        if (IsDotNetSerializationConstructor(ctorDecl, context))
        {
            context.Diagnostics.Info(
                ".NET serialization constructor omitted; Java serialization does not use SerializationInfo/StreamingContext constructors.",
                ctorDecl.GetLocation());
            return new JavaMemberCollection();
        }

        var ctorSymbol = context.SemanticModel?.GetDeclaredSymbol(ctorDecl) as IMethodSymbol;
        context.EnterMethod(ctorSymbol);

        var className = context.CurrentType?.Name ?? ctorDecl.Identifier.Text;

        var javaCtor = new JavaConstructorDeclaration
        {
            ClassName = className,
            Modifiers = ConvertModifiers(ctorDecl.Modifiers),
            LeadingComment = context.GetDeclarationComments(ctorDecl, ctorSymbol).ToCombinedComment()
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
        if (ctorDecl.Initializer != null)
        {
            var args = GetInitializerArguments(ctorDecl.Initializer, context);

            // Java implicitly calls super() only for constructors with no explicit initializer.
            // Preserve explicit C# base(...) calls even when the sole argument is null.
            if (args.Count > 0)
            {
                if (ctorDecl.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword))
                {
                    javaCtor.Initializer = $"this({string.Join(", ", args)})";
                }
                else if (ctorDecl.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.BaseKeyword))
                {
                    javaCtor.Initializer = $"super({string.Join(", ", args)})";
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
        if (bodyStatements.Count > 0)
        {
            javaCtor.StructuredBody = new Java.JavaMethodBody(
                bodyStatements.Select(s => (Java.JavaStatement)new Java.JavaRawStatement(s)));
        }
        else if (ctorDecl.Body != null || javaCtor.Initializer != null)
        {
            // Empty block body or initializer-only constructor → generate a body, not an abstract semicolon.
            javaCtor.StructuredBody = new Java.JavaMethodBody();
        }

        // Issue 5: For 'protected internal', annotate the body so readers know the access intent.
        if (IsProtectedInternal(ctorDecl.Modifiers))
        {
            var comment = "// C# 'protected internal' → Java 'protected' (package-private semantic is implicit via protected)";
            var commentStmt = new Java.JavaRawStatement(comment);
            if (javaCtor.StructuredBody == null)
            {
                javaCtor.StructuredBody = new Java.JavaMethodBody([commentStmt]);
            }
            else
            {
                javaCtor.StructuredBody.Statements.Insert(0, commentStmt);
            }
        }

        // Generate overloads for C# default parameters
        var allCtorParams = ctorDecl.ParameterList?.Parameters.ToList() ?? new List<ParameterSyntax>();
        var ctorOverloads = Utilities.DefaultParameterHelper.GenerateConstructorOverloads(
            allCtorParams,
            javaCtor,
            context,
            ExpressionTransformerFacade.Instance);

        JavaSyntaxNode result;
        if (ctorOverloads.Count > 0)
        {
            var allDeclarations = new List<JavaSyntaxNode> { javaCtor };
            allDeclarations.AddRange(ctorOverloads);
            result = new JavaMemberCollection(allDeclarations);
        }
        else
        {
            result = javaCtor;
        }

        context.LeaveMethod();
        return result;
    }

    private List<string> GetInitializerArguments(ConstructorInitializerSyntax initializer, ConversionContext context)
    {
        // Resolve the target constructor symbol for argument type coercion
        // (e.g., arrays passed to IEnumerable<T> params need ArrayHelper.toList() wrapping).
        IMethodSymbol? ctorSymbol = null;
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(initializer);
            ctorSymbol = symbolInfo.Symbol as IMethodSymbol;
        }

        var transformed = ArgumentTransformer.TransformArgumentList(
            initializer.ArgumentList,
            context,
            ExpressionTransformerFacade.Instance,
            argStartIndex: 0,
            methodSymbol: ctorSymbol);

        return string.IsNullOrWhiteSpace(transformed)
            ? new List<string>()
            : SplitTopLevelArguments(transformed);
    }

    private static List<string> SplitTopLevelArguments(string arguments)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < arguments.Length; i++)
        {
            var ch = arguments[i];
            if (inString)
            {
                escaped = ch == '\\' && !escaped;
                if (ch == '"' && !escaped)
                    inString = false;
                if (ch != '\\')
                    escaped = false;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(arguments[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        result.Add(arguments[start..].Trim());
        return result.Where(arg => arg.Length > 0).ToList();
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

    private static bool IsDotNetSerializationConstructor(
        ConstructorDeclarationSyntax ctorDecl,
        ConversionContext context)
    {
        var parameters = ctorDecl.ParameterList?.Parameters;
        if (parameters == null || parameters.Value.Count != 2)
            return false;

        if (ctorDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            return false;

        return IsSerializationType(parameters.Value[0].Type, context, "SerializationInfo")
            && IsSerializationType(parameters.Value[1].Type, context, "StreamingContext");
    }

    private static bool IsSerializationType(
        TypeSyntax? typeSyntax,
        ConversionContext context,
        string expectedName)
    {
        if (typeSyntax == null)
            return false;

        var type = context.SemanticModel?.GetTypeInfo(typeSyntax).Type;
        if (type != null)
        {
            var display = type.ToDisplayString();
            if (display == $"System.Runtime.Serialization.{expectedName}"
                || display == expectedName)
            {
                return true;
            }
        }

        var syntaxText = typeSyntax.ToString();
        return syntaxText == expectedName
            || syntaxText.EndsWith($".{expectedName}", StringComparison.Ordinal);
    }
}
