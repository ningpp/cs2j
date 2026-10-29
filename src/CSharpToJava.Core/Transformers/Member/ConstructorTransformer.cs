using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Utilities;
using System.Text.RegularExpressions;

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
                var statements = statementTransformer.TransformStatements(ctorDecl.Body.Statements, context);
                foreach (var rawStatement in statements)
                {
                    // A C# static constructor may contain bare `return;` statements
                    // (e.g. early-exit guards), which are legal in C# constructors but
                    // illegal inside a Java `static { }` block ("return outside method").
                    // They are almost always the last statement of a guard branch, so
                    // dropping them preserves the guard semantics for the common case.
                    var statement = rawStatement;
                    if (statement != null && Regex.IsMatch(statement, @"^\s*return\s*;\s*$"))
                        continue;
                    if (statement != null && statement.Contains("return;", StringComparison.Ordinal))
                        statement = Regex.Replace(statement, @"\breturn\s*;", "");
                    staticBlock.Statements.Add(statement);
                }
            }
            return staticBlock;
        }

        var isSerializationShapedCtor = IsSerializationShapedConstructor(ctorDecl, context);

        // .NET serialization constructors take (SerializationInfo, StreamingContext).
        // Java has no equivalent: java.io.Serializable does not use such a constructor,
        // and emitting it cannot work because the mapped base (Exception -> RuntimeException)
        // has no matching (Object, Object) constructor, so even a pure base-forwarding
        // ctor would produce uncompilable super(...) calls. Omit it unconditionally.
        if (isSerializationShapedCtor && !IsPublicSerializationConstructor(ctorDecl))
        {
            context.Diagnostics.Info(
                ".NET serialization constructor omitted; Java serialization does not use SerializationInfo/StreamingContext constructors.",
                ctorDecl.GetLocation());
            return new JavaMemberCollection();
        }

        var ctorSymbol = context.GetDeclaredSymbol(ctorDecl) as IMethodSymbol;
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
            var typeInfo = context.GetTypeInfo(param.Type!);
            var javaType = typeInfo.Type != null ? context.MapType(typeInfo.Type) : "Object";

            // Escape Java keywords in parameter names (e.g., '_' is reserved in Java 9+)
            var paramName = ConversionContext.EscapeJavaKeyword(param.Identifier.Text);
            var javaParam = new JavaParameter(javaType, paramName);

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

        // Two C# constructors may erase to the same Java signature (e.g.
        // Rectangle(IEnumerable<Point>) and Rectangle(IEnumerable<Rectangle>)).
        // Java cannot rename constructors; give the later one a marker parameter
        // and make ObjectCreationTransformer pass it at the resolved call sites.
        if (ctorSymbol != null && HasEarlierErasedDuplicateConstructor(ctorSymbol, context))
        {
            javaCtor.Parameters.Add(new JavaParameter("boolean", "__cs2jCtorMarker"));
            context.RegisterErasureConflictConstructor(ctorSymbol);
        }

        // 处理初始值设定项
        if (ctorDecl.Initializer != null)
        {
            var args = GetInitializerArguments(ctorDecl.Initializer, context);

            if (isSerializationShapedCtor
                && ctorDecl.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.BaseKeyword))
            {
                // Public serialization-shaped ctors must survive for call sites, but the
                // generated Java base never declares a (SerializationInfo, StreamingContext)
                // ctor, so base(info, context) has no super() target. Skip the forwarding
                // and emit a body that mirrors the C# behavior instead (see below).
                context.Diagnostics.Info(
                    "Base-forwarding dropped for .NET serialization constructor; emitting PlatformNotSupportedException body instead.",
                    ctorDecl.GetLocation());
            }
            else if (ctorDecl.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword))
            {
                javaCtor.Initializer = $"this({string.Join(", ", args)})";
            }
            else if (ctorDecl.Initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.BaseKeyword))
            {
                if (args.Count > 0)
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
            var expr = ctorDecl.ExpressionBody.Expression;
            // Tuple assignments in expression bodies — (X, Y) = (x, y) — must go through
            // the statement transformer so each element follows the normal assignment path
            // (auto-property writes map to backing fields, etc.).
            if (expr is AssignmentExpressionSyntax assignExpr
                && assignExpr.Left is TupleExpressionSyntax assignTupleLeft
                && assignTupleLeft.Arguments.Count > 0
                && assignExpr.Right is TupleExpressionSyntax assignTupleRight
                && assignTupleRight.Arguments.Count == assignTupleLeft.Arguments.Count
                && assignTupleLeft.Arguments.All(a => a.Expression is not DeclarationExpressionSyntax))
            {
                bodyStatements.Add(Transformers.Statement.StatementTransformer.TupleAssignmentSplitters
                    .SplitTupleAssignment(assignExpr, context));
            }
            else
            {
                // Issue 4: Validate that the expression is void-compatible before emitting as a statement.
                var exprTransformer = ExpressionTransformerFacade.Instance;
                var transformed = exprTransformer.Transform(expr, context);
                bool isVoidCompatible = expr is ThrowExpressionSyntax
                    || expr is AssignmentExpressionSyntax
                    || expr is InvocationExpressionSyntax
                    || expr is PostfixUnaryExpressionSyntax
                    || expr is PrefixUnaryExpressionSyntax;
                if (isVoidCompatible)
                    bodyStatements.Add(transformed + ";");
                else
                    bodyStatements.Add($"// TODO: verify expression body semantics: {transformed};");
            }
        }

        // 组合初始值设定项和主体
        if (isSerializationShapedCtor && bodyStatements.Count == 0)
        {
            // No forwardable base ctor exists; reproduce the .NET behavior (the base
            // serialization ctor throws PlatformNotSupportedException) directly.
            bodyStatements.Add("throw new PlatformNotSupportedException();");
        }

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
            var comment = "// C# 'protected internal' → Java 'public' (cross-package internal callers need access)";
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

        // A C# 'internal' constructor is emitted as Java 'public' so cross-package callers stay
        // reachable, but the runtime's accessibility questions must still see the C# answer:
        // .NET's collection converter rejects a type whose parameterless constructor is not
        // public. The marker carries that fact so TypeHelper.getConstructorInfo can honour it.
        if (ctorDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword))
            && !ctorDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
        {
            javaCtor.Annotations.Add(new Java.JavaAnnotation("CompatInternalCtor"));
            context.AddImport("io.github.ningpp.compat.CompatInternalCtor");
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
            var symbolInfo = context.GetSymbolInfo(initializer);
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

        // C# "protected internal" means protected OR internal. Since Java has no direct equivalent
        // and internal maps to public, emit public so cross-package callers can access the member.
        if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
            result &= ~JavaModifiers.Protected;

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
        if (IsPublicSerializationConstructor(ctorDecl))
            return false;

        return IsSerializationShapedConstructor(ctorDecl, context);
    }

    private static bool IsPublicSerializationConstructor(ConstructorDeclarationSyntax ctorDecl)
        => ctorDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));

    private static bool IsSerializationShapedConstructor(
        ConstructorDeclarationSyntax ctorDecl,
        ConversionContext context)
    {
        var parameters = ctorDecl.ParameterList?.Parameters;
        if (parameters == null || parameters.Value.Count != 2)
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

        var type = context.GetTypeInfo(typeSyntax).Type;
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

    private static bool HasEarlierErasedDuplicateConstructor(
        IMethodSymbol ctor, ConversionContext context)
    {
        if (ctor.Locations.Length == 0)
            return false;
        var start = ctor.Locations[0].SourceSpan.Start;
        var signature = ErasedConstructorSignature(ctor, context);
        return ctor.ContainingType.InstanceConstructors.Any(other =>
            !SymbolEqualityComparer.Default.Equals(other, ctor)
            && other.Locations.Length > 0
            && other.Locations[0].SourceSpan.Start < start
            && ErasedConstructorSignature(other, context) == signature);
    }

    private static string ErasedConstructorSignature(
        IMethodSymbol ctor, ConversionContext context)
        => string.Join(",", ctor.Parameters.Select(parameter =>
        {
            var mapped = context.MapType(parameter.Type);
            var angle = mapped.IndexOf('<');
            return angle < 0 ? mapped : mapped[..angle];
        }));
}
