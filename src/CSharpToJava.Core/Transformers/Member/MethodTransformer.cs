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

        // C06 R8 opt-in FFM 白名单通道：unsafe-ffm 预处理把命中的
        // [DllImport]/[LibraryImport] extern 归一为带
        // [Cs2j.Generation.InteropFacade] 标记的响亮 C# stub；项目管线
        // 在此发射 MethodHandle 字段 + invokeExact 方法体（v1 ABI 子集：
        // 基元 + 只读 string）。
        if (TryGetInteropFacade(sym, out var library, out var entryPoint, out var stringEncoding))
        {
            var facade = BuildFfmFacade(m, sym!, library!, entryPoint!, stringEncoding, ctx);
            ctx.LeaveMethod();
            return facade;
        }

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

    // ================== C06 R8 FFM 白名单 facade（opt-in） ==================

    private const string FfmFacadeAttributeName =
        "Cs2j.Generation.InteropFacadeAttribute";

    /// <summary>
    /// 方法是否带 unsafe-ffm 发射的 InteropFacade 标记；返回白名单
    /// library/entryPoint 与字符串编码（utf8/utf16）。
    /// </summary>
    private static bool TryGetInteropFacade(
        IMethodSymbol? sym,
        out string? library,
        out string? entryPoint,
        out string stringEncoding)
    {
        library = null;
        entryPoint = null;
        stringEncoding = "utf8";

        var attr = sym?.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == FfmFacadeAttributeName);
        if (attr == null) return false;

        if (attr.ConstructorArguments.Length < 2
            || attr.ConstructorArguments[0].Value is not string lib
            || attr.ConstructorArguments[1].Value is not string ep)
        {
            return false;
        }

        library = lib;
        entryPoint = ep;
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "StringEncoding" && named.Value.Value is string enc)
                stringEncoding = enc;
        }

        return true;
    }

    /// <summary>
    /// 发射 FFM facade：<c>private static final MethodHandle</c> 绑定字段 +
    /// invokeExact 包装方法。形参/返回的 Java 声明类型沿用项目常规映射
    /// （byte/ushort→int 等），ABI 边界在方法体内显式窄化到 ValueLayout
    /// 载体类型，保证调用点类型兼容且 invokeExact 签名精确。
    /// </summary>
    private static JavaMemberCollection BuildFfmFacade(
        MethodDeclarationSyntax m,
        IMethodSymbol sym,
        string library,
        string entryPoint,
        string stringEncoding,
        ConversionContext ctx)
    {
        var javaName = GetJavaMethodName(m.Identifier.Text, sym, ctx);
        // 同名重载（含同 arity）也必须得到不同字段：追加参数类型签名。
        var signature = string.Join("_", sym.Parameters.Select(FfmParameterTag));
        var handleName = "__cs2jFfm_" + m.Identifier.Text + "_" + sym.Parameters.Length
            + (signature.Length > 0 ? "_" + signature : "");
        var utf16 = stringEncoding == "utf16";

        var paramLayouts = new List<string>();
        var javaParameters = new List<JavaParameter>();
        var invokeArguments = new List<string>();
        var preStatements = new List<string>();
        var hasString = false;

        foreach (var parameter in sym.Parameters)
        {
            var declaredType = ctx.MapType(parameter.Type);
            javaParameters.Add(new JavaParameter(
                declaredType, ConversionContext.EscapeJavaKeyword(parameter.Name)));

            if (parameter.Type.SpecialType == SpecialType.System_String)
            {
                hasString = true;
                paramLayouts.Add("ValueLayout.ADDRESS");
                var segmentName = "__cs2jSeg_" + parameter.Name;
                preStatements.Add(
                    $"MemorySegment {segmentName} = FfmImports.{(utf16 ? "utf16" : "utf8")}({parameter.Name});");
                invokeArguments.Add(segmentName);
            }
            else
            {
                paramLayouts.Add(FfmAbiLayout(parameter.Type));
                invokeArguments.Add(FfmNarrowArgument(parameter.Type, parameter.Name));
            }
        }

        var isVoid = sym.ReturnType.SpecialType == SpecialType.System_Void;
        var descriptor = isVoid
            ? "FunctionDescriptor.ofVoid(" + string.Join(", ", paramLayouts) + ")"
            : paramLayouts.Count == 0
                ? "FunctionDescriptor.of(" + FfmAbiLayout(sym.ReturnType) + ")"
                : "FunctionDescriptor.of(" + FfmAbiLayout(sym.ReturnType) + ", "
                    + string.Join(", ", paramLayouts) + ")";

        var field = new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Private | JavaModifiers.Static | JavaModifiers.Final,
            Type = "MethodHandle",
            Name = handleName,
            Initializer = $"FfmImports.bind(\"{EscapeJavaString(library)}\", " +
                          $"\"{EscapeJavaString(entryPoint)}\", {descriptor})",
        };

        var invoke = $"{handleName}.invokeExact({string.Join(", ", invokeArguments)})";
        var bodyLines = new List<string>(preStatements)
        {
            "try",
            "{",
        };
        if (isVoid)
        {
            bodyLines.Add(invoke + ";");
        }
        else
        {
            // invokeExact 必须显式窄化到布局载体；载体与声明类型不同时
            // （byte/char→int）由 Java 隐式拓宽回方法返回类型。
            bodyLines.Add($"return ({FfmCarrierJavaType(sym.ReturnType)}) {invoke};");
        }

        bodyLines.AddRange(new[]
        {
            "} catch (RuntimeException | Error e) {",
            "    throw e;",
            "} catch (Throwable t) {",
            "    throw new RuntimeException(t);",
            "}",
        });

        var method = new JavaMethodDeclaration
        {
            Name = javaName,
            Modifiers = ConvertModifiers(m.Modifiers),
            ReturnType = isVoid ? "void" : ctx.MapType(sym.ReturnType),
            Body = string.Join("\n", bodyLines),
        };
        method.Parameters.AddRange(javaParameters);

        ctx.AddImport("io.github.ningpp.compat.FfmImports");
        ctx.AddImport("java.lang.invoke.MethodHandle");
        ctx.AddImport("java.lang.foreign.FunctionDescriptor");
        ctx.AddImport("java.lang.foreign.ValueLayout");
        if (hasString) ctx.AddImport("java.lang.foreign.MemorySegment");

        ctx.RegisterMethodDeclaration(sym, method);

        return new JavaMemberCollection(new List<JavaSyntaxNode> { field, method });
    }

    /// <summary>ABI 布局（D10：char=UTF-16 双字节；bool v1 已在 C# 侧 UF061 拒绝）。</summary>
    private static string FfmAbiLayout(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Byte or SpecialType.System_SByte => "ValueLayout.JAVA_BYTE",
        SpecialType.System_Int16 => "ValueLayout.JAVA_SHORT",
        SpecialType.System_UInt16 or SpecialType.System_Char => "ValueLayout.JAVA_CHAR",
        SpecialType.System_Int32 or SpecialType.System_UInt32 => "ValueLayout.JAVA_INT",
        SpecialType.System_Int64 or SpecialType.System_UInt64 => "ValueLayout.JAVA_LONG",
        SpecialType.System_Single => "ValueLayout.JAVA_FLOAT",
        SpecialType.System_Double => "ValueLayout.JAVA_DOUBLE",
        _ => "ValueLayout.JAVA_INT",
    };

    /// <summary>布局载体的 Java 类型（invokeExact 实参/返回转换目标）。</summary>
    private static string FfmCarrierJavaType(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Byte or SpecialType.System_SByte => "byte",
        SpecialType.System_Int16 => "short",
        SpecialType.System_UInt16 or SpecialType.System_Char => "char",
        SpecialType.System_Int32 or SpecialType.System_UInt32 => "int",
        SpecialType.System_Int64 or SpecialType.System_UInt64 => "long",
        SpecialType.System_Single => "float",
        SpecialType.System_Double => "double",
        _ => "int",
    };

    /// <summary>
    /// 实参进入 invokeExact 前的窄化：项目常规映射把 C# byte/ushort 声明为
    /// Java int，而 JAVA_BYTE/JAVA_CHAR 的载体分别是 byte/char。
    /// </summary>
    private static string FfmNarrowArgument(ITypeSymbol type, string argumentName) => type.SpecialType switch
    {
        SpecialType.System_Byte => $"(byte) {argumentName}",
        SpecialType.System_UInt16 => $"(char) {argumentName}",
        _ => argumentName,
    };

    /// <summary>MethodHandle 字段名参数标签：string 用固定标签，其余用 SpecialType 序号。</summary>
    private static string FfmParameterTag(IParameterSymbol parameter) =>
        parameter.Type.SpecialType == SpecialType.System_String
            ? "str"
            : ((int)parameter.Type.SpecialType).ToString();

    private static string EscapeJavaString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

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
