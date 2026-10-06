using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Type;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>方法转换器：C# 方法声明 → Java 方法声明</summary>
public class MethodTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext ctx)
    {
        if (node is not MethodDeclarationSyntax m)
            throw new ArgumentException($"Expected MethodDeclarationSyntax, got {node.GetType()}");

        var sym = ctx.GetDeclaredSymbol(m) as IMethodSymbol;
        ctx.EnterMethod(sym);

        var method = new JavaMethodDeclaration
        {
            Name = GetJavaMethodName(m.Identifier.Text, sym, ctx),
            Modifiers = ConvertModifiers(m.Modifiers),
            ReturnType = MapReturnType(m.ReturnType, ctx),
            LeadingComment = ctx.GetDeclarationComments(m, sym).ToCombinedComment()
        };
        if (m.Modifiers.Any(t => t.IsKind(SyntaxKind.AsyncKeyword))) method.IsAsync = true;

        BuildSignature(method, m.TypeParameterList, m.ConstraintClauses, m.ParameterList, ctx);
        BuildBody(method, m.Body, m.ExpressionBody, ctx);
        if (sym != null) ctx.RegisterMethodDeclaration(sym, method);

        // 局部函数提升为类级 private 方法（Java 无局部函数），嵌套函数迭代排空
        var extras = new List<JavaSyntaxNode>();
        var worklist = new Queue<LocalFunctionStatementSyntax>();
        foreach (var f in ctx.MethodState.DrainHoistedLocalFunctions()) worklist.Enqueue(f);
        while (worklist.Count > 0)
        {
            var fn = worklist.Dequeue();
            extras.Add(TransformLocalFunction(fn, ctx));
            foreach (var nested in ctx.MethodState.DrainHoistedLocalFunctions()) worklist.Enqueue(nested);
        }

        ctx.LeaveMethod();
        return extras.Count > 0
            ? new JavaMemberCollection(new[] { method }.Concat(extras).ToList())
            : method;
    }

    /// <summary>局部函数提升：固定 private，外层为静态方法时一并 static。</summary>
    internal static JavaMethodDeclaration TransformLocalFunction(LocalFunctionStatementSyntax fn, ConversionContext ctx)
    {
        var sym = ctx.GetDeclaredSymbol(fn) as IMethodSymbol;
        var isStatic = fn.Modifiers.Any(t => t.IsKind(SyntaxKind.StaticKeyword))
            || (sym?.ContainingSymbol as IMethodSymbol)?.IsStatic == true;
        var method = new JavaMethodDeclaration
        {
            Name = sym != null && ctx.TryGetHoistedLocalFunctionName(sym, out var registered)
                ? registered : GetLocalFunctionJavaName(fn),
            Modifiers = JavaModifiers.Private | (isStatic ? JavaModifiers.Static : JavaModifiers.None),
            ReturnType = MapReturnType(fn.ReturnType, ctx)
        };
        BuildSignature(method, fn.TypeParameterList, fn.ConstraintClauses, fn.ParameterList, ctx);

        // 提升的局部函数是独立 Java 方法：按自身符号重算 async 上下文，转换完恢复外层值。
        var previousAsyncContext = ctx.IsInAsyncContext;
        ctx.IsInAsyncContext = sym is { IsAsync: true }
            && AsyncNormalize.AsyncShapeClassifier.IsTaskLike(sym.ReturnType);
        try
        {
            BuildBody(method, fn.Body, fn.ExpressionBody, ctx);
        }
        finally
        {
            ctx.IsInAsyncContext = previousAsyncContext;
        }
        return method;
    }

    internal static string GetLocalFunctionJavaName(LocalFunctionStatementSyntax fn)
    {
        var name = fn.Identifier.Text;
        return ConversionContext.EscapeJavaKeyword(name.Length > 0
            ? char.ToLowerInvariant(name[0]) + name[1..] : name);
    }

    /// <summary>类内方法转换后的 Java 名集合，供提升的局部函数避名。</summary>
    internal static HashSet<string> GetClassMethodJavaNames(INamedTypeSymbol type, ConversionContext ctx)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_"))
                names.Add(char.ToUpper(m.Name[4]) + m.Name[5..]);
            else if (m.MethodKind != MethodKind.UserDefinedOperator)
                names.Add(JavaName(m.Name));
        }
        return names;
    }

    /// <summary>表达式体的集合包装已精简，原样返回。</summary>
    internal static string WrapExpressionBodyForIterableReturn(
        string exprBody, ExpressionSyntax _, TypeSyntax __, ConversionContext ___) => exprBody;

    /// <summary>MSTest DeploymentItem 注解已精简，不再生成。</summary>
    internal static JavaAnnotation? BuildDeploymentItemAnnotation(AttributeSyntax _, ConversionContext __) => null;

    private static void BuildSignature(JavaMethodDeclaration jm, TypeParameterListSyntax? typeParams,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraints, ParameterListSyntax? parameters, ConversionContext ctx)
    {
        foreach (var tp in typeParams?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
            jm.TypeParameters.Add(new JavaTypeParameter(tp.Identifier.Text));
        if (constraints.Count > 0)
            ClassTransformer.ApplyTypeParameterConstraints(constraints, jm.TypeParameters, ctx);
        foreach (var p in parameters?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
            if (ConvertParameter(p, ctx) is { } jp) jm.Parameters.Add(jp);
    }

    private static void BuildBody(JavaMethodDeclaration jm, BlockSyntax? body,
        ArrowExpressionClauseSyntax? arrow, ConversionContext ctx)
    {
        var facade = ExpressionTransformerFacade.Instance;
        if (body != null)
        {
            jm.StructuredBody = new Transformers.Statement.StatementTransformer().TransformBlockToStructuredBody(body, ctx);
            AppendAsyncTrailingReturn(jm, body, ctx);
        }
        else if (arrow?.Expression is ThrowExpressionSyntax thrown)
        {
            jm.Body = $"throw {facade.Transform(thrown.Expression, ctx)};";
        }
        else if (arrow != null)
        {
            var expr = facade.Transform(arrow.Expression, ctx);
            // async Task/Task<T> 表达式体：返回类型是 CompletableFuture，需包 completedFuture。
            if (ctx.IsInAsyncContext)
            {
                ctx.AddImport("java.util.concurrent.CompletableFuture");
                expr = $"CompletableFuture.completedFuture({expr})";
            }

            // 表达式转换可能排出前置语句（如属性 ++/-- 降级出的临时量、as 表达式
            // 提升）。表达式体没有语句锚点，必须在此展开为块体，否则前置语句会被
            // 静默丢弃而只留下引用未定义临时量的 return（对齐 PropertyTransformer
            // getter 表达式体的既有处理）。
            if (ctx.HasPendingPreStatements)
            {
                var lines = ctx.DrainPreStatements()
                    .Select(s => s.TrimEnd(';') + ";")
                    .ToList();
                var tail = jm.ReturnType == "void"
                    ? $"{expr.TrimEnd(';')};"
                    : $"return {expr.TrimEnd(';')};";
                lines.Add(tail);
                jm.Body = string.Join("\n", lines);
                jm.IsBodyExpression = false;
            }
            else
            {
                jm.Body = expr;
                jm.IsBodyExpression = true;
            }
        }
    }

    /// <summary>
    /// async Task/Task&lt;T&gt;/ValueTask(T) 方法端点可达时（C# 允许 async Task 方法省略
    /// 尾 return），Java 返回类型已是 CompletableFuture，必须补
    /// <c>return CompletableFuture.completedFuture(null);</c>，否则输出不可编译。
    /// 端点不可达（每个路径已 return/throw）时不补——Java 对不可达语句是编译错误。
    /// 可达性以 Roslyn 控制流分析为准，不做文本猜测。
    /// </summary>
    private static void AppendAsyncTrailingReturn(
        JavaMethodDeclaration jm, BlockSyntax body, ConversionContext ctx)
    {
        if (!ctx.IsInAsyncContext || ctx.SemanticModel == null || jm.StructuredBody == null)
            return;

        bool endPointReachable;
        try
        {
            endPointReachable = ctx.SemanticModel.AnalyzeControlFlow(body).EndPointIsReachable;
        }
        catch (ArgumentException)
        {
            // 语义模型与树不匹配（预处理边界）时保守不补。
            return;
        }
        if (!endPointReachable)
            return;

        ctx.AddImport("java.util.concurrent.CompletableFuture");
        jm.StructuredBody.Statements.Add(
            new Transformers.Statement.JavaStatementNode(
                "return CompletableFuture.completedFuture(null);"));
    }

    private static string GetJavaMethodName(string name, IMethodSymbol? sym, ConversionContext ctx)
    {
        if (name.StartsWith("get_") || name.StartsWith("set_"))
            return char.ToUpper(name[4]) + name[5..];
        var mapped = sym?.ContainingType != null
            ? ctx.TypeMappings.MapMethod(sym.ContainingType.ToDisplayString(), name) : null;
        return !string.IsNullOrEmpty(mapped) ? mapped : JavaName(name);
    }

    private static string MapReturnType(TypeSyntax returnType, ConversionContext ctx)
    {
        var type = returnType is RefTypeSyntax refType ? refType.Type : returnType;
        if (type is PredefinedTypeSyntax pre && pre.Keyword.IsKind(SyntaxKind.VoidKeyword)) return "void";
        var info = ctx.GetTypeInfo(type);
        return info.Type != null ? ctx.MapType(info.Type) : ctx.MapTypeFromSyntax(type);
    }

    private static JavaParameter? ConvertParameter(ParameterSyntax param, ConversionContext ctx)
    {
        var info = ctx.GetTypeInfo(param.Type!);
        var javaType = info.Type is not null and not IErrorTypeSymbol
            ? ctx.MapType(info.Type) : ctx.MapTypeFromSyntax(param.Type!);
        var result = new JavaParameter(javaType, ConversionContext.EscapeJavaKeyword(param.Identifier.Text));
        foreach (var mod in param.Modifiers)
        {
            if (mod.IsKind(SyntaxKind.RefKeyword) || mod.IsKind(SyntaxKind.OutKeyword))
                result = new JavaParameter(HolderTypeResolver.GetHolderType(javaType), result.Name);
            else if (mod.IsKind(SyntaxKind.ParamsKeyword)) result.IsVarArgs = true;
        }
        return result;
    }

    private static JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        var result = JavaModifiers.None;
        foreach (var mod in modifiers)
        {
            result |= mod.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.StaticKeyword => JavaModifiers.Static,
                SyntaxKind.OverrideKeyword or SyntaxKind.NewKeyword => JavaModifiers.Override,
                SyntaxKind.AbstractKeyword => JavaModifiers.Abstract,
                SyntaxKind.SealedKeyword => JavaModifiers.Final,
                SyntaxKind.ExternKeyword => JavaModifiers.Native,
                _ => JavaModifiers.None
            };
        }
        if (result.HasFlag(JavaModifiers.Protected) && result.HasFlag(JavaModifiers.Public))
            result &= ~JavaModifiers.Protected;   // protected internal → public
        return result;
    }

    private static string JavaName(string name) => ConversionContext.EscapeJavaKeyword(name switch
    {
        "GetHashCode" => "hashCode",
        "GetEnumerator" => "iterator",
        "GetType" => "getClass",
        "Dispose" => "close",
        "ToLower" or "ToLowerInvariant" => "toLowerCase",
        "ToUpper" or "ToUpperInvariant" => "toUpperCase",
        _ => name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name
    });
}
