using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.Core.Transformers.Utilities;

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

        // Serialization surfaces (ISerializable.GetObjectData et al.) map their
        // parameter/return types to the "__suppress__" sentinel: no Java equivalent
        // exists, so the whole member is dropped instead of emitting unresolvable
        // type names in the signature.
        if (HasSuppressedSignature(sym, ctx))
        {
            ctx.LeaveMethod();
            return new JavaMemberCollection();
        }

        // A public CompareTo(object) alongside CompareTo(T) clashes with the
        // javac-generated Comparable<T>.compareTo(Object) bridge. The typed
        // overload carries the same semantics for the generic interface.
        if (sym is { Name: "CompareTo", Parameters.Length: 1 } objectCompare
            && objectCompare.Parameters[0].Type.SpecialType == SpecialType.System_Object
            && objectCompare.ContainingType.GetMembers("CompareTo")
                .OfType<IMethodSymbol>()
                .Any(candidate => !SymbolEqualityComparer.Default.Equals(candidate, objectCompare)
                    && candidate.Parameters.Length == 1
                    && candidate.Parameters[0].Type.SpecialType != SpecialType.System_Object))
        {
            ctx.LeaveMethod();
            return new JavaMemberCollection();
        }

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

        var javaMethodName = GetJavaMethodName(m.Identifier.Text, sym, ctx);
        if (sym != null)
        {
            if (sym.OverriddenMethod is { } overriddenBase)
            {
                var baseDeclaration = ctx.FindMethodDeclaration(overriddenBase);
                if (baseDeclaration != null)
                    javaMethodName = baseDeclaration.Name;
                else if (TryPredictErasureConflictName(overriddenBase, ctx, out var predictedBaseName))
                    javaMethodName = predictedBaseName;
                else if (ctx.TryGetErasureConflictMethodName(sym, out var renamedMethod))
                    javaMethodName = renamedMethod;
            }
            else if (ctx.TryGetErasureConflictMethodName(sym, out var renamedMethod2))
            {
                javaMethodName = renamedMethod2;
            }
        }

        var method = new JavaMethodDeclaration
        {
            Name = javaMethodName,
            Modifiers = ConvertModifiers(m.Modifiers),
            ReturnType = MapReturnType(m.ReturnType, ctx),
            LeadingComment = ctx.GetDeclarationComments(m, sym).ToCombinedComment()
        };

        // C# explicit interface implementations (e.g. `void IDisposable.Dispose()`)
        // are private by declaration, but the emitted class implements the mapped
        // Java interface (AutoCloseable etc.), where the method MUST be public —
        // javac rejects a package-private override of an interface method.
        if (sym is { ExplicitInterfaceImplementations.Length: > 0 }
            && !m.Modifiers.Any(t => t.IsKind(SyntaxKind.PublicKeyword)
                                     || t.IsKind(SyntaxKind.PrivateKeyword)
                                     || t.IsKind(SyntaxKind.ProtectedKeyword)
                                     || t.IsKind(SyntaxKind.InternalKeyword)))
        {
            method.Modifiers |= JavaModifiers.Public;
        }
        // C# `protected override` of a `protected internal` / `internal` base
        // member is legal (family-or-assembly accessibility), but the base maps
        // to public in Java (assembly visibility has no JVM analogue) and javac
        // rejects an override that weakens access. Widen the override to match
        // the overridden member's mapped accessibility.
        if (m.Modifiers.Any(t => t.IsKind(SyntaxKind.OverrideKeyword))
            && sym?.OverriddenMethod is { } overridden
            && overridden.DeclaredAccessibility
                is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal
            && !method.Modifiers.HasFlag(JavaModifiers.Public))
        {
            method.Modifiers &= ~(JavaModifiers.Private | JavaModifiers.Protected);
            method.Modifiers |= JavaModifiers.Public;
        }
        if (m.Modifiers.Any(t => t.IsKind(SyntaxKind.AsyncKeyword))) method.IsAsync = true;

        BuildSignature(method, m.TypeParameterList, m.ConstraintClauses, m.ParameterList, ctx);
        ApplyBoxedGenericOverrideSignature(method, sym, ctx);
        TryRenameForErasureConflict(method, sym, ctx);
        BuildBody(method, m.Body, m.ExpressionBody, ctx);
        // Java forbids static members from referencing class type parameters;
        // re-declare the enclosing ones this static method uses as its own.
        StaticMemberTypeParameterHelper.PromoteReferencedEnclosingTypeParameters(
            method, m, sym?.IsStatic == true, sym, ctx);
        // Unimplemented C# partial method declarations have no body; the C# compiler
        // elides their calls. Emit an empty body so the Java declaration stays valid
        // (method.Body would otherwise render a bodyless, abstract-looking member).
        if ((m.Modifiers.Any(t => t.IsKind(SyntaxKind.PartialKeyword))
             || sym?.IsPartialDefinition == true)
            && m.Body == null && m.ExpressionBody == null
            && sym?.PartialImplementationPart == null)
        {
            method.Body = "{\n}";
        }
        // Any other source-declared bodyless instance/static member (e.g. partial
        // declarations whose symbol lost the partial flag after text round-trips)
        // must still be a concrete Java method; C# elides calls to such partials.
        if (method.Body == null && method.StructuredBody == null
            && sym is { IsAbstract: false }
            && sym.ContainingType?.TypeKind != TypeKind.Interface
            && !m.Modifiers.Any(t => t.IsKind(SyntaxKind.AbstractKeyword)
                                     || t.IsKind(SyntaxKind.ExternKeyword)))
        {
            method.Body = "{\n}";
        }

        // 显式接口实现（桥）：Java 返回类型必须采用映射接口成员的形状（接口映射
        // 权威），否则 javac 对同擦除签名报"返回类型不兼容"硬错。
        ApplyExplicitInterfaceMemberShape(method, sym, ctx);
        ApplyDictionaryEnumeratorShape(method, sym, ctx);

        // xUnit/MSTest 测试方法 → JUnit 5 测试方法 + 静态数据 provider。
        var testMembers = ApplyTestMethodAnnotations(m, method, ctx);

        // C# 程序入口 Main：JVM 只能从 `public static void main(String[])` 启动。
        // `private static void Main()` 必须提升为 public、补齐 String[] args；
        // `static int Main(...)` 降级为 private mainCore + void main 包一层
        // System.exit，保留进程退出码语义。
        JavaSyntaxNode? entryWrapper = null;
        if (IsCompilationEntryPoint(sym, ctx))
            entryWrapper = NormalizeEntryPoint(method, sym);

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
        var members = new List<JavaSyntaxNode> { method };
        if (entryWrapper != null) members.Add(entryWrapper);
        members.AddRange(testMembers);
        members.AddRange(extras);
        return members.Count > 1
            ? new JavaMemberCollection(members)
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
        // Hoisted local functions become static class methods when their outer
        // member is static; the same Java type-parameter promotion applies.
        StaticMemberTypeParameterHelper.PromoteReferencedEnclosingTypeParameters(
            method, fn, isStatic, sym, ctx);
        return method;
    }

    internal static string GetLocalFunctionJavaName(LocalFunctionStatementSyntax fn)
    {
        var name = fn.Identifier.Text;
        return ConversionContext.EscapeJavaKeyword(name.Length > 0
            ? char.ToLowerInvariant(name[0]) + name[1..] : name);
    }

    /// <summary>
    /// 判断方法是否是当前编译（程序）的入口点。C# 允许库内存在同名普通
    /// <c>Main</c>，只有 <see cref="CSharpCompilation.GetEntryPoint"/> 命中的
    /// 方法才按 JVM 入口规范化，避免改动其他 Main 方法的签名/可见性。
    /// </summary>
    private static bool IsCompilationEntryPoint(IMethodSymbol? sym, ConversionContext ctx)
    {
        if (sym is not { IsStatic: true, Name: "Main", MethodKind: MethodKind.Ordinary })
            return false;
        if (sym.Parameters.Length is not (0 or 1))
            return false;
        if (sym.Parameters.Length == 1
            && (sym.Parameters[0].Type is not IArrayTypeSymbol stringArray
                || stringArray.ElementType.SpecialType != SpecialType.System_String))
        {
            return false;
        }

        var compilation = ctx.ProjectCompilation ?? ctx.SemanticModel?.Compilation;
        if (compilation == null)
            return IsCanonicalMainShape(sym);

        IMethodSymbol? entry;
        try
        {
            entry = compilation.GetEntryPoint(default);
        }
        catch (Exception)
        {
            // 多入口/无入口等情况下 GetEntryPoint 会抛错，下面走形状判定。
            entry = null;
        }

        if (entry != null)
            return SymbolEqualityComparer.Default.Equals(entry, sym);

        // 部分合并/临时编译往往以 Dll OutputKind 构建，GetEntryPoint 不可用。
        // 可执行输出直接信任经典 Main 形状；其余输出也按 C# 约定兜底——
        // static Main()/Main(string[])/int Main 是 C# 保留的入口签名。
        return IsCanonicalMainShape(sym);
    }

    private static bool IsCanonicalMainShape(IMethodSymbol sym)
        => sym.ReturnsVoid
           || sym.ReturnType.SpecialType == SpecialType.System_Int32;

    /// <summary>
    /// 把 C# 入口方法规范化成 JVM 入口形状。void 入口就地改名为 public
    /// main 并补 String[] 参数；int 入口保留为 private mainCore（同样补
    /// args 形参），另返回一个 void main 包装方法经 System.exit 透传退出码。
    /// </summary>
    private static JavaSyntaxNode? NormalizeEntryPoint(JavaMethodDeclaration method, IMethodSymbol sym)
    {
        var hasArgs = method.Parameters.Count == 1;
        if (!hasArgs)
            method.Parameters.Add(new JavaParameter("String[]", "args"));

        method.Modifiers &= ~(JavaModifiers.Private | JavaModifiers.Protected);
        method.Modifiers |= JavaModifiers.Public | JavaModifiers.Static;

        if (sym.ReturnsVoid)
        {
            method.Name = "main";
            return null;
        }

        // int Main(...): 原方法体保持不变地留在 mainCore，main 仅做退出码桥接。
        method.Name = "mainCore";
        method.Modifiers &= ~JavaModifiers.Public;
        method.Modifiers |= JavaModifiers.Private;

        var wrapper = new JavaMethodDeclaration
        {
            Name = "main",
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = "void",
            Body = "System.exit(mainCore(args));",
        };
        wrapper.Parameters.Add(new JavaParameter("String[]", "args"));
        return wrapper;
    }


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

    private static void ApplyDictionaryEnumeratorShape(
        JavaMethodDeclaration jm, IMethodSymbol? sym, ConversionContext ctx)
    {
        if (sym is not { Name: "GetEnumerator" } || jm.ReturnType != "CSharpDictEnumerator")
            return;
        var containing = sym.ContainingType;
        if (containing == null
            || !containing.AllInterfaces.Any(i =>
                i.ToDisplayString() == "System.Collections.IDictionary"))
        {
            return;
        }
        // CSharpIDictionary.dictEnumerator() declares Generic<CSharpDictEntry>:
        // the adapter both satisfies the generic enumerator slot and keeps the
        // non-generic dictionary face, so every implementation must match it.
        jm.ReturnType = "CSharpDictEnumerator.Generic<CSharpDictEntry>";
        ctx.AddImport("io.github.ningpp.compat.CSharpDictEnumerator");
        ctx.AddImport("io.github.ningpp.compat.CSharpDictEntry");
    }

    /// <summary>
    /// Java erases a generic base member's <c>T</c> parameter to <c>Object</c>, so
    /// an override specialized to a value type cannot declare the primitive (its
    /// erasure would clash without overriding). Emit such parameters as
    /// <c>Object</c> and remember the boxed wrapper the body must cast back to;
    /// boxed return types override covariantly as-is.
    /// </summary>
    private static void ApplyBoxedGenericOverrideSignature(
        JavaMethodDeclaration jm, IMethodSymbol? sym, ConversionContext ctx)
    {
        if (sym?.OverriddenMethod is not { } overridden)
            return;
        var original = overridden.OriginalDefinition;
        if (original.ContainingType?.TypeParameters.Length is not > 0)
            return;

        int count = Math.Min(original.Parameters.Length, jm.Parameters.Count);
        for (int i = 0; i < count; i++)
        {
            if (original.Parameters[i].Type is not ITypeParameterSymbol)
                continue;
            if (i >= sym.Parameters.Length
                || sym.Parameters[i].Type is ITypeParameterSymbol
                || !sym.Parameters[i].Type.IsValueType)
                continue;
            jm.Parameters[i].Type = ctx.MapTypeBoxed(sym.Parameters[i].Type);
        }

        if (original.ReturnType is ITypeParameterSymbol
            && sym.ReturnType is not ITypeParameterSymbol
            && sym.ReturnType.IsValueType)
            jm.ReturnType = ctx.MapTypeBoxed(sym.ReturnType);
        else if (original.ReturnType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && nullable.TypeArguments[0] is ITypeParameterSymbol
            && sym.ReturnType is not ITypeParameterSymbol
            && sym.ReturnType.IsValueType)
            jm.ReturnType = ctx.MapTypeBoxed(sym.ReturnType);
    }

    private static void BuildSignature(JavaMethodDeclaration jm, TypeParameterListSyntax? typeParams,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraints, ParameterListSyntax? parameters, ConversionContext ctx)
    {
        foreach (var tp in typeParams?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
            jm.TypeParameters.Add(new JavaTypeParameter(tp.Identifier.Text));
        if (constraints.Count > 0)
            ClassTransformer.ApplyTypeParameterConstraints(constraints, jm.TypeParameters, ctx);
        foreach (var p in parameters?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
            if (ConvertParameter(p, ctx) is { } jp)
            {
                jm.Parameters.Add(jp);
                // Track ref/out parameters that the Java declaration wraps in a
                // holder type so the body's expression-level reads emit `.value`.
                if (HolderTypeResolver.IsHolderType(jp.Type)
                    && ctx.GetDeclaredSymbol(p) is IParameterSymbol holderParam)
                {
                    ctx.MethodState.RegisterHolderValueParam(holderParam);
                }
            }
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
        else if (arrow?.Expression is AssignmentExpressionSyntax assignmentBody
            && assignmentBody.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            BuildAssignmentExpressionBody(jm, assignmentBody, ctx);
        }
        else if (arrow != null)
        {
            var expr = facade.Transform(arrow.Expression, ctx);
            // array/string → Span<T>/ReadOnlySpan<T> 表达式体返回（如
            // `static ReadOnlySpan<int> Category() => new byte[]{...}`）。
            var arrowTypeInfo = ctx.GetTypeInfo(arrow.Expression);
            expr = SpanCoercion.CoerceOrSame(
                arrow.Expression, expr, arrowTypeInfo.ConvertedType ?? arrowTypeInfo.Type, ctx);
            // IEnumerable 家族表达式体返回的载体适配（生成集合/数组/流管道 → 
            // CSharpGenericIterable），与 return 语句路径同一规则。
            expr = IterableCoercion.CoerceOrSame(
                arrow.Expression, expr, ctx.CurrentMethod?.ReturnType, ctx);
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
    /// C# 表达式体为赋值（<c>T M() =&gt; P = v;</c>）：赋值表达式的值就是赋入值，
    /// 但 Java 赋值没有值（展开后的属性 setter 还是 void）。展开为块体：先执行
    /// 赋值语句，再按左值重新读取（属性走 getter）作为返回值。真实链
    /// closure-lift 生成的 <c>object Lambda_...() =&gt; Regex.CacheSize = -1;</c>
    /// （保留属性不再由 member-desugar 展开为值保持的 C# 块体）。
    /// </summary>
    private static void BuildAssignmentExpressionBody(
        JavaMethodDeclaration jm, AssignmentExpressionSyntax assignment, ConversionContext ctx)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var statement = facade.Transform(assignment, ctx).TrimEnd(';');
        var preBefore = ctx.HasPendingPreStatements ? ctx.DrainPreStatements().ToList() : new List<string>();
        // 赋值表达式的值 = 赋入值：按值位置重读左值（属性 → getX()）。
        var value = IdentifierExpressionTransformer
            .TransformAssignmentTargetAsValue(assignment.Left, ctx);
        var preAfter = ctx.HasPendingPreStatements ? ctx.DrainPreStatements().ToList() : new List<string>();

        var lines = new List<string>();
        lines.AddRange(preBefore.Select(s => s.TrimEnd(';') + ";"));
        lines.Add(statement + ";");
        lines.AddRange(preAfter.Select(s => s.TrimEnd(';') + ";"));
        if (jm.ReturnType == "void")
        {
            jm.Body = string.Join("\n", lines);
            jm.IsBodyExpression = false;
            return;
        }
        if (ctx.IsInAsyncContext)
        {
            ctx.AddImport("java.util.concurrent.CompletableFuture");
            value = $"CompletableFuture.completedFuture({value})";
        }
        lines.Add($"return {value};");
        jm.Body = string.Join("\n", lines);
        jm.IsBodyExpression = false;
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

    // ================== xUnit / MSTest → JUnit 5 ==================

    /// <summary>
    /// 把 xUnit/MSTest 测试特性映射成 JUnit 5：Fact→@Test、Theory→@ParameterizedTest、
    /// InlineData/MemberData/ClassData→静态 @MethodSource provider。JUnit 测试方法必须
    /// 是实例方法，因此这里顺带去掉 static；provider 本身保持 static。
    /// </summary>
    internal static List<JavaSyntaxNode> ApplyTestMethodAnnotations(
        MethodDeclarationSyntax methodDecl, JavaMethodDeclaration javaMethod, ConversionContext ctx)
    {
        var providers = new List<JavaSyntaxNode>();
        var attributes = methodDecl.AttributeLists.SelectMany(al => al.Attributes).ToList();
        if (attributes.Count == 0)
            return providers;

        var names = attributes
            .Select(a => NormalizeAttributeName(a.Name.ToString()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (names.Contains("ClassInitialize"))
        {
            // MSTest 类级初始化 → JUnit @BeforeAll。TestContext 形参由
            // MSTestExtension 的参数解析器注入。
            AddAnnotationOnce(javaMethod, "BeforeAll", ctx, "org.junit.jupiter.api.BeforeAll");
            ctx.CurrentClassNeedsMSTestExtension = true;
            return providers;
        }

        if (names.Contains("ClassCleanup"))
        {
            AddAnnotationOnce(javaMethod, "AfterAll", ctx, "org.junit.jupiter.api.AfterAll");
            ctx.CurrentClassNeedsMSTestExtension = true;
            return providers;
        }

        if (names.Contains("TestInitialize"))
        {
            AddAnnotationOnce(javaMethod, "BeforeEach", ctx, "org.junit.jupiter.api.BeforeEach");
            if (!string.IsNullOrEmpty(javaMethod.Body) && !javaMethod.IsBodyExpression
                && (javaMethod.Modifiers & JavaModifiers.Static) == 0)
            {
                var close = javaMethod.Body.LastIndexOf('}');
                if (close >= 0)
                {
                    javaMethod.Body = javaMethod.Body[..close]
                        + "this.testContext = Microsoft.VisualStudio.TestTools.UnitTesting.TestContext.create();\n"
                        + javaMethod.Body[close..];
                }
            }
            return providers;
        }

        if (names.Contains("TestCleanup"))
        {
            AddAnnotationOnce(javaMethod, "AfterEach", ctx, "org.junit.jupiter.api.AfterEach");
            return providers;
        }

        var isFact = names.Contains("Fact") || names.Contains("SkippableFact") || names.Contains("TestMethod");
        var isTheory = names.Contains("Theory") || names.Contains("SkippableTheory") || names.Contains("DataTestMethod");
        if (!isFact && !isTheory)
            return providers;

        var inlineRows = attributes
            .Where(a => IsAttribute(a, "InlineData") || IsAttribute(a, "DataRow"))
            .Select(a => a.ArgumentList?.Arguments.Select(x => x.Expression).ToList() ?? new List<ExpressionSyntax>())
            .ToList();
        var memberDataAttrs = attributes.Where(a => IsAttribute(a, "MemberData")).ToList();
        var classDataAttrs = attributes.Where(a => IsAttribute(a, "ClassData")).ToList();
        var xmlTestsAttrs = attributes.Where(a => IsAttribute(a, "XmlTests")).ToList();

        var disabled = names.Contains("Ignore")
            || HasSkipArgument(attributes, ctx)
            || IsSkippedForCurrentPlatform(attributes, ctx);

        // JUnit 5 只发现实例测试方法。
        javaMethod.Modifiers &= ~JavaModifiers.Static;

        if (isTheory)
            AddAnnotationOnce(javaMethod, "ParameterizedTest", ctx, "org.junit.jupiter.params.ParameterizedTest");
        else if (ctx.GetDeclaredSymbol(methodDecl) is IMethodSymbol { ContainingType: { } testOwner }
            && testOwner.GetTypeMembers("Test").Any())
            // A nested type named Test shadows the imported annotation in Java
            // (and the single-type import itself would clash), so qualify it.
            AddAnnotationOnce(javaMethod, "org.junit.jupiter.api.Test", ctx, string.Empty);
        else
            AddAnnotationOnce(javaMethod, "Test", ctx, "org.junit.jupiter.api.Test");

        if (disabled)
            AddAnnotationOnce(javaMethod, "Disabled", ctx, "org.junit.jupiter.api.Disabled");

        var expectedException = attributes.FirstOrDefault(a => IsAttribute(a, "ExpectedException"));
        if (expectedException != null)
            WrapExpectedException(javaMethod, expectedException, ctx);

        // Fact 方法上的 InlineData/MemberData 在 xUnit 里同样不生效；只有 Theory 需要数据源。
        if (!isTheory || (inlineRows.Count == 0 && memberDataAttrs.Count == 0 && classDataAttrs.Count == 0 && xmlTestsAttrs.Count == 0))
            return providers;

        var providerName = UniqueProviderName(javaMethod.Name, ctx);
        AddAnnotationOnce(javaMethod, $"MethodSource(\"{providerName}\")", ctx,
            "org.junit.jupiter.params.provider.MethodSource");
        ctx.AddImport("org.junit.jupiter.params.provider.Arguments");

        // xUnit 用声明的默认值补足行里省略的尾部可选参数；JUnit 不会。
        // InlineData 行是编译期常量（PadWithOptionalDefaults 静态补齐），而
        // MemberData/ClassData 的行只有运行时才知道长度，故在 provider 里用
        // XunitArguments.pad 补齐。
        var (firstOptionalIndex, optionalDefaults) = CollectOptionalTrailingDefaults(methodDecl, ctx);
        var testRowArguments = firstOptionalIndex >= 0 && optionalDefaults.Count > 0
            ? $"XunitArguments.pad(__row, {firstOptionalIndex}, {string.Join(", ", optionalDefaults)})"
            : "__row";
        if (firstOptionalIndex >= 0 && optionalDefaults.Count > 0)
            ctx.AddImport("csharp.xunit.XunitArguments");

        // xUnit 还会把行值转换成声明的形参类型（long → int、枚举常量 → long）；
        // JUnit 只做拓宽转换。InlineData 行在渲染期逐参数转换（保持既有
        // Arguments.of(...) 形状）；运行期行（MemberData/ClassData）只有列值，
        // 交给 XunitArguments.coerce。
        var parameterTypes = TestParameterTypesLiteral(methodDecl, ctx);
        if (parameterTypes != null)
        {
            ctx.AddImport("csharp.xunit.XunitArguments");
            testRowArguments = $"XunitArguments.coerce({testRowArguments}, {parameterTypes})";
        }

        var body = new StringBuilder();
        var usesList = memberDataAttrs.Count > 0 || classDataAttrs.Count > 0 || xmlTestsAttrs.Count > 0;
        if (usesList)
        {
            body.AppendLine("var __rows = new java.util.ArrayList<Arguments>();");
            foreach (var row in inlineRows)
                body.AppendLine($"__rows.add({BuildArgumentsOf(PadWithOptionalDefaults(row, methodDecl), ctx, methodDecl)});");
            foreach (var attr in memberDataAttrs)
                AppendMemberDataRows(body, attr, ctx, testRowArguments);
            foreach (var attr in classDataAttrs)
                AppendClassDataRows(body, attr, ctx, testRowArguments);
            foreach (var attr in xmlTestsAttrs)
                AppendXmlTestsRows(body, attr, methodDecl, ctx, testRowArguments);
            body.Append("return __rows.stream();");
        }
        else
        {
            var rows = inlineRows.Select(r => BuildArgumentsOf(PadWithOptionalDefaults(r, methodDecl), ctx, methodDecl)).ToList();
            body.Append("return java.util.stream.Stream.of(")
                .Append(string.Join(", ", rows))
                .Append(");");
        }

        providers.Add(new JavaMethodDeclaration
        {
            Name = providerName,
            Modifiers = JavaModifiers.Private | JavaModifiers.Static,
            ReturnType = "java.util.stream.Stream<Arguments>",
            Body = body.ToString(),
        });
        return providers;
    }

    private static void AddAnnotationOnce(
        JavaMethodDeclaration method, string annotation, ConversionContext ctx, string import)
    {
        var baseName = annotation.Contains('(') ? annotation[..annotation.IndexOf('(')] : annotation;
        var alreadyPresent = method.Annotations.Any(a =>
            a.Name == annotation
            || (a.Name.Contains('(') ? a.Name[..a.Name.IndexOf('(')] : a.Name) == baseName);
        if (alreadyPresent)
            return;

        method.Annotations.Add(new JavaAnnotation(annotation));
        if (!string.IsNullOrEmpty(import))
            ctx.AddImport(import);
    }

    /// <summary>
    /// MSTest <c>[ExpectedException(typeof(X))]</c>：JUnit 5 没有 expected 属性，
    /// 把方法体包进 <c>Assertions.assertThrows(X.class, () -&gt; { ... })</c>。
    /// </summary>
    private static void WrapExpectedException(
        JavaMethodDeclaration method, AttributeSyntax attribute, ConversionContext ctx)
    {
        var first = attribute.ArgumentList?.Arguments.FirstOrDefault();
        if (first?.Expression is not TypeOfExpressionSyntax typeOf)
            return;

        var javaType = ResolveExpectedExceptionJavaType(typeOf, ctx);
        if (javaType == null)
            return;

        string body;
        if (method.StructuredBody != null)
        {
            body = method.StructuredBody.ToBodyString();
            method.StructuredBody = null;
        }
        else
        {
            body = method.Body ?? string.Empty;
        }

        method.IsBodyExpression = false;
        method.Body =
            $"org.junit.jupiter.api.Assertions.assertThrows({javaType}.class, () -> {{\n{body}\n}});";
    }

    private static string? ResolveExpectedExceptionJavaType(
        TypeOfExpressionSyntax typeOf, ConversionContext ctx)
    {
        var symbol = ctx.GetTypeInfo(typeOf.Type).Type as INamedTypeSymbol;
        var name = symbol?.Name ?? typeOf.Type.ToString();
        if (ExpectedExceptionJavaTypes.TryGetValue(name, out var mapped))
            return mapped;

        if (symbol is { TypeKind: not TypeKind.Error })
            return ctx.MapType(symbol);

        return name;
    }

    /// <summary>
    /// .NET exception → Java exception，与 <c>ExceptionApiRewriter</c> 的
    /// throw/catch 映射保持一致，使 assertThrows 的类字面量编译且能捕获。
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedExceptionJavaTypes = new(StringComparer.Ordinal)
    {
        ["NotImplementedException"] = "UnsupportedOperationException",
        ["NotSupportedException"] = "UnsupportedOperationException",
        ["PlatformNotSupportedException"] = "UnsupportedOperationException",
        ["InvalidOperationException"] = "IllegalStateException",
        ["ObjectDisposedException"] = "IllegalStateException",
        ["ArgumentException"] = "IllegalArgumentException",
        ["ArgumentNullException"] = "IllegalArgumentException",
        ["ArgumentOutOfRangeException"] = "IllegalArgumentException",
        ["FormatException"] = "IllegalArgumentException",
        ["InvalidDataException"] = "IllegalArgumentException",
        ["OverflowException"] = "ArithmeticException",
        ["NotFiniteNumberException"] = "ArithmeticException",
        ["OperationCanceledException"] = "java.util.concurrent.CancellationException",
        ["TaskCanceledException"] = "java.util.concurrent.CancellationException",
    };

    private static string UniqueProviderName(string methodName, ConversionContext ctx)
    {
        var baseName = "__cs2jData_" + methodName;
        var name = baseName;
        var suffix = 2;
        while (ctx.CurrentType is JavaClassDeclaration cls
               && cls.Methods.Any(m => m.Name == name))
        {
            name = $"{baseName}_{suffix++}";
        }
        return name;
    }

    /// <summary>
    /// 收集尾部可选参数的默认值表达式（按参数顺序），供运行时数据源补齐行使用。
    /// 可选参数之后若还有必选参数，xUnit 的补齐语义不再成立，返回 (-1, 空)。
    /// </summary>
    private static (int FirstIndex, IReadOnlyList<string> Defaults) CollectOptionalTrailingDefaults(
        MethodDeclarationSyntax methodDecl, ConversionContext ctx)
    {
        var parameters = methodDecl.ParameterList.Parameters;
        var firstOptional = -1;
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Default != null)
            {
                firstOptional = i;
                break;
            }
        }
        if (firstOptional < 0)
            return (-1, Array.Empty<string>());

        var defaults = new List<string>(parameters.Count - firstOptional);
        for (var i = firstOptional; i < parameters.Count; i++)
        {
            var defaultValue = parameters[i].Default?.Value;
            if (defaultValue == null)
                return (-1, Array.Empty<string>());
            defaults.Add(RenderTestDataExpression(defaultValue, ctx));
        }
        return (firstOptional, defaults);
    }

    /// <summary>
    /// xUnit lets an InlineData row omit trailing optional parameters and fills in
    /// the declared default; JUnit supplies no such mechanism and fails with
    /// ParameterResolutionException. Append each omitted parameter's default
    /// expression so the generated Arguments.of row matches the method arity.
    /// </summary>
    private static IReadOnlyList<ExpressionSyntax> PadWithOptionalDefaults(
        IReadOnlyList<ExpressionSyntax> row, MethodDeclarationSyntax methodDecl)
    {
        var parameters = methodDecl.ParameterList.Parameters;
        if (row.Count >= parameters.Count)
            return row;

        var padded = new List<ExpressionSyntax>(row);
        for (int i = row.Count; i < parameters.Count; i++)
        {
            var defaultValue = parameters[i].Default?.Value;
            if (defaultValue == null)
                return row; // required parameter missing: keep xUnit's arity untouched
            padded.Add(defaultValue);
        }
        return padded;
    }

    private static string BuildArgumentsOf(
        IReadOnlyList<ExpressionSyntax> args, ConversionContext ctx, MethodDeclarationSyntax? methodDecl = null)
    {
        if (args.Count == 0)
            return "Arguments.of()";

        var rendered = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            IParameterSymbol? parameter = null;
            if (methodDecl != null && i < methodDecl.ParameterList.Parameters.Count)
                parameter = ctx.GetDeclaredSymbol(methodDecl.ParameterList.Parameters[i]) as IParameterSymbol;
            rendered.Add(RenderTestArgument(args[i], ctx, parameter));
        }

        return "Arguments.of(" + string.Join(", ", rendered) + ")";
    }

    /// <summary>
    /// InlineData 行在渲染期完成 xUnit 式转换：枚举常量 → 数值形参用
    /// EnumHelper.toInt64，long → int 等收窄用 Java 强转；类型兼容时保持原样，
    /// 避免改变既有 Arguments.of(...) 的形状。
    /// </summary>
    private static string RenderTestArgument(
        ExpressionSyntax arg, ConversionContext ctx, IParameterSymbol? parameter = null)
    {
        var expression = RenderTestDataExpression(arg, ctx);
        var argumentType = ctx.GetTypeInfo(arg).Type;
        if (parameter != null && argumentType != null && argumentType is not ITypeParameterSymbol)
        {
            var target = parameter.Type;
            if (IsCoercibleNumericTarget(target) && argumentType.TypeKind == TypeKind.Enum)
            {
                ctx.AddImport("io.github.ningpp.compat.EnumHelper");
                expression = $"EnumHelper.toInt64({expression})";
            }
            else if (IsCoercibleNumericTarget(target)
                && NarrowingCast(target.SpecialType) is { } cast
                && NumericRank(argumentType.SpecialType) > NumericRank(target.SpecialType))
            {
                expression = $"(({cast}) {expression})";
            }
        }
        // 单个 null 字面量 / 数组参数必须显式转成 Object，避免 Arguments.of
        // 把数组当成 varargs 展开或让 null 产生重载歧义。
        if (arg is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }
            || ctx.GetTypeInfo(arg).Type is IArrayTypeSymbol)
        {
            expression = "(Object) " + expression;
        }
        return expression;
    }

    private static bool IsCoercibleNumericTarget(ITypeSymbol type)
        => type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
            or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Char;

    private static string? NarrowingCast(SpecialType target) => target switch
    {
        SpecialType.System_Int32 => "int",
        SpecialType.System_Int64 => "long",
        SpecialType.System_Int16 => "short",
        SpecialType.System_Byte => "byte",
        SpecialType.System_SByte => "byte",
        SpecialType.System_Single => "float",
        SpecialType.System_Double => "double",
        SpecialType.System_Char => "char",
        _ => null,
    };

    private static int NumericRank(SpecialType type) => type switch
    {
        SpecialType.System_SByte or SpecialType.System_Byte => 1,
        SpecialType.System_Int16 or SpecialType.System_Char => 2,
        SpecialType.System_Int32 => 3,
        SpecialType.System_Int64 => 4,
        SpecialType.System_Single => 5,
        SpecialType.System_Double => 6,
        _ => 0,
    };

    /// <summary>
    /// 有「需要 xUnit 式行值转换」的形参时返回 Java 形参类型数组字面量，否则 null。
    /// 数值基础类型与 C# 枚举需要转换（long → int、枚举常量 → long 等）；其余类型
    /// JUnit 的隐式转换足够。
    /// </summary>
    private static string? TestParameterTypesLiteral(MethodDeclarationSyntax methodDecl, ConversionContext ctx)
    {
        var parameters = methodDecl.ParameterList.Parameters;
        var needsCoercion = false;
        var literals = new List<string>(parameters.Count);
        foreach (var parameter in parameters)
        {
            var type = ctx.GetTypeInfo(parameter.Type).Type;
            if (type == null || type is ITypeParameterSymbol)
            {
                literals.Add("Object.class");
                continue;
            }
            if (type.TypeKind == TypeKind.Enum
                || type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
                    or SpecialType.System_Int16 or SpecialType.System_Byte
                    or SpecialType.System_SByte or SpecialType.System_Single
                    or SpecialType.System_Double or SpecialType.System_Char)
            {
                needsCoercion = true;
            }
            literals.Add(ConversionContext.GetClassLiteral(type, ctx));
        }
        if (!needsCoercion || literals.Count == 0)
            return null;
        return $"new Class[]{{{string.Join(", ", literals)}}}";
    }

    private static string RenderTestDataExpression(ExpressionSyntax expression, ConversionContext ctx)
    {
        var rendered = ExpressionTransformerFacade.Instance.Transform(expression, ctx);
        // InlineData 只接受编译期常量；表达式转换理论上不会产生前置语句，保险起见丢弃。
        if (ctx.HasPendingPreStatements) ctx.DrainPreStatements();
        if (ctx.HasPendingPostStatements) ctx.DrainPostStatements();
        return rendered;
    }

    private static void AppendMemberDataRows(
        StringBuilder body, AttributeSyntax attr, ConversionContext ctx, string testRowArguments)
    {
        var memberName = ExtractFirstMemberName(attr);
        if (string.IsNullOrEmpty(memberName))
            return;

        // xUnit MemberData can forward extra attribute arguments to the provider:
        // [MemberData(nameof(Transform), nameof(TestData), 3)].
        var extraArguments = attr.ArgumentList?.Arguments.Skip(1)
            .Where(a => a.NameEquals == null && a.NameColon == null)
            .Select(a => ExpressionTransformerFacade.Instance.Transform(a.Expression, ctx))
            .ToList() ?? new List<string>();

        var owner = ResolveMemberDataOwner(attr, ctx);
        var (call, resultType) = ResolveDataMemberCall(memberName!, owner, ctx, extraArguments);
        AppendEnumerationLoop(body, call, resultType, ctx, testRowArguments);
    }

    private static void AppendClassDataRows(
        StringBuilder body, AttributeSyntax attr, ConversionContext ctx, string testRowArguments)
    {
        var typeOf = attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression as TypeOfExpressionSyntax;
        if (typeOf == null)
            return;

        if (ctx.GetTypeInfo(typeOf.Type).Type is not INamedTypeSymbol dataType)
            return;

        var mapped = ctx.MapType(dataType);
        AppendEnumerationLoop(body, $"new {mapped}()", dataType, ctx, testRowArguments);
    }

    /// <summary>
    /// [XmlTests(nameof(Create))] is a custom xUnit DataAttribute that calls
    /// XmlInlineDataDiscoverer.generateTestCases(Supplier&lt;CTestModule&gt;) to
    /// produce IEnumerable&lt;object[]&gt; test case rows. Generate the equivalent
    /// Java call and feed it through the same enumeration loop as MemberData.
    /// </summary>
    private static void AppendXmlTestsRows(
        StringBuilder body, AttributeSyntax attr, MethodDeclarationSyntax methodDecl, ConversionContext ctx,
        string testRowArguments)
    {
        var memberName = ExtractFirstMemberName(attr);
        if (string.IsNullOrEmpty(memberName))
            return;

        // Resolve the class name that owns the Create() method (the declaring
        // type of the test method, mapped to its Java name).
        var methodSymbol = ctx.SemanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
        var ownerType = methodSymbol?.ContainingType;
        var javaOwner = ownerType != null ? ctx.MapType(ownerType) : methodDecl.Identifier.Text;
        // Map the C# method name to its Java equivalent (PascalCase -> camelCase).
        var javaMethodName = char.ToLowerInvariant(memberName![0]) + memberName[1..];
        var call = $"OLEDB.Test.ModuleCore.XmlInlineDataDiscoverer.generateTestCases(() -> {javaOwner}.{javaMethodName}())";
        // Result type is CSharpGenericIterable<Object[]> (from generateTestCases).
        AppendEnumerationLoop(body, call, null, ctx, testRowArguments);
    }

    /// <summary>
    /// xUnit 数据源是 IEnumerable&lt;object[]&gt;，转换后为 CSharpGenericIterable（不是
    /// java.lang.Iterable），必须走 iterator()/moveNext()；数组与 java.util 集合走 for-each。
    /// </summary>
    private static void AppendEnumerationLoop(
        StringBuilder body, string source, ITypeSymbol? sourceType, ConversionContext ctx, string testRowArguments)
    {
        var mapped = sourceType != null ? ctx.MapType(sourceType) : string.Empty;

        if (sourceType is IArrayTypeSymbol || mapped.EndsWith("[]", StringComparison.Ordinal))
        {
            body.AppendLine($"for (Object[] __row : {source})");
            body.AppendLine("{");
            body.AppendLine($"    __rows.add(Arguments.of({testRowArguments}));");
            body.AppendLine("}");
            return;
        }

        if (mapped.StartsWith("List<", StringComparison.Ordinal)
            || mapped.StartsWith("java.util.", StringComparison.Ordinal)
            || mapped.StartsWith("Iterable<", StringComparison.Ordinal))
        {
            body.AppendLine($"for (Object[] __row : {source})");
            body.AppendLine("{");
            body.AppendLine($"    __rows.add(Arguments.of({testRowArguments}));");
            body.AppendLine("}");
            return;
        }

        // CSharpGenericIterable / CSharpGenericIList / 未知 IEnumerable 实现。
        var enumerator = ctx.GenerateSyntheticName("__cs2jDataEnum");
        body.AppendLine($"var {enumerator} = {source}.iterator();");
        body.AppendLine($"while ({enumerator}.moveNext())");
        body.AppendLine("{");
        body.AppendLine($"    Object[] __row = {enumerator}.getCurrent();");
        body.AppendLine($"    __rows.add(Arguments.of({testRowArguments}));");
        body.AppendLine("}");
    }

    private static string? ExtractFirstMemberName(AttributeSyntax attr)
    {
        var first = attr.ArgumentList?.Arguments.FirstOrDefault();
        return first == null ? null : SimpleMemberName(first.Expression);
    }

    private static string? SimpleMemberName(ExpressionSyntax expression) => expression switch
    {
        InvocationExpressionSyntax inv when inv.ArgumentList.Arguments.Count > 0
            => SimpleMemberName(inv.ArgumentList.Arguments[0].Expression),
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) => lit.Token.ValueText,
        _ => null,
    };

    private static INamedTypeSymbol? ResolveMemberDataOwner(AttributeSyntax attr, ConversionContext ctx)
    {
        foreach (var arg in attr.ArgumentList?.Arguments ?? default)
        {
            if (arg.NameEquals?.Name.Identifier.Text == "MemberType"
                && arg.Expression is TypeOfExpressionSyntax typeOf
                && ctx.GetTypeInfo(typeOf.Type).Type is INamedTypeSymbol memberType)
            {
                return memberType;
            }
        }
        return ctx.CurrentEnclosingRoslynType;
    }

    private static (string Call, ITypeSymbol? ResultType) ResolveDataMemberCall(
        string memberName, INamedTypeSymbol? owner, ConversionContext ctx,
        IReadOnlyList<string>? extraArguments = null)
    {
        var args = extraArguments is { Count: > 0 }
            ? "(" + string.Join(", ", extraArguments) + ")"
            : "()";
        if (owner == null)
            return (memberName + args, null);

        var prefix = string.Empty;
        if (ctx.CurrentEnclosingRoslynType is { } current
            && !SymbolEqualityComparer.Default.Equals(owner, current))
        {
            prefix = ctx.MapType(owner) + ".";
        }

        foreach (var member in owner.GetMembers(memberName))
        {
            switch (member)
            {
                case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary:
                    return (prefix + GetJavaMethodName(method.Name, method, ctx) + args, method.ReturnType);
                case IPropertySymbol property:
                    return (prefix + JavaNaming.GetPropertyGetterName(ConversionContext.EscapeJavaKeyword(property.Name)) + "()",
                        property.Type);
                case IFieldSymbol field:
                    return (prefix + ConversionContext.EscapeJavaKeyword(field.Name), field.Type);
            }
        }

        return (prefix + memberName + args, null);
    }

    private static bool HasSkipArgument(IEnumerable<AttributeSyntax> attributes, ConversionContext ctx)
    {
        foreach (var attr in attributes)
        {
            foreach (var arg in attr.ArgumentList?.Arguments ?? default)
            {
                if (arg.NameEquals?.Name.Identifier.Text != "Skip")
                    continue;
                var value = ConstantStringValue(arg.Expression, ctx);
                if (!string.IsNullOrEmpty(value))
                    return true;
            }
        }
        return false;
    }

    private static bool IsSkippedForCurrentPlatform(
        IEnumerable<AttributeSyntax> attributes, ConversionContext ctx)
    {
        foreach (var attr in attributes)
        {
            var name = NormalizeAttributeName(attr.Name.ToString());
            var first = attr.ArgumentList?.Arguments.FirstOrDefault();
            if (first == null)
                continue;

            if (name.Equals("PlatformSpecific", StringComparison.OrdinalIgnoreCase))
            {
                // 当前 Java 目标运行在 Windows；其余平台变体不执行。
                var platform = ConstantStringValue(first.Expression, ctx);
                if (!string.Equals(platform, "Windows", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (name.Equals("SkipOnTargetFramework", StringComparison.OrdinalIgnoreCase))
            {
                // 仅 netframework 的差异在 .NET Core/Java 目标上不需要跳过。
                var framework = ConstantStringValue(first.Expression, ctx);
                if (!string.Equals(framework, "netframework", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string? ConstantStringValue(ExpressionSyntax expression, ConversionContext ctx)
    {
        if (expression is LiteralExpressionSyntax lit)
        {
            return lit.IsKind(SyntaxKind.NullLiteralExpression) ? null : lit.Token.ValueText;
        }

        if (ctx.SemanticModel?.GetConstantValue(expression) is { HasValue: true, Value: string value })
            return value;

        var text = expression.ToString();
        var dot = text.LastIndexOf('.');
        return dot >= 0 ? text[(dot + 1)..] : text.Trim('"');
    }

    private static bool IsAttribute(AttributeSyntax attr, string name)
        => NormalizeAttributeName(attr.Name.ToString()).Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAttributeName(string rawName)
    {
        var name = rawName.Trim();
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
            name = name[(lastDot + 1)..];
        if (name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
            name = name[..^9];
        return name;
    }

    internal static string GetJavaMethodName(string name, IMethodSymbol? sym, ConversionContext ctx)
    {
        if (name.StartsWith("get_") || name.StartsWith("set_"))
            return char.ToUpper(name[4]) + name[5..];
        // A non-generic IDictionary implementation declares only
        // GetEnumerator() returning IDictionaryEnumerator. Emitting it as
        // iterator() would clash with CSharpCollection.iterator()'s
        // CSharpGenericEnumerator return type; the compat interface supplies a
        // default iterator(), so this member keeps its dictionary name.
        if (sym is { Name: "GetEnumerator" }
            && sym.ReturnType is INamedTypeSymbol
                { Name: "IDictionaryEnumerator", ContainingNamespace: { } dictionaryNamespace }
            && dictionaryNamespace.ToDisplayString() == "System.Collections"
            && sym.ContainingType?.GetMembers("GetEnumerator").OfType<IMethodSymbol>().Count() == 1)
        {
            return "dictEnumerator";
        }
        var mapped = sym?.ContainingType != null
            ? ctx.TypeMappings.MapMethod(sym.ContainingType.ToDisplayString(), name) : null;
        if (string.IsNullOrEmpty(mapped))
            mapped = TryMapImplicitInterfaceImplementation(name, sym, ctx);
        var javaName = !string.IsNullOrEmpty(mapped) ? mapped : JavaName(name);
        // compat 集合实现类的非 bool/void Remove 与接口的 boolean remove(Object)
        // 擦除后同签名但返回类型不兼容，必须改名。后缀由符号形状确定性推导（与
        // CompatCollectionConformance.IsRenamableRemove 同一判定）：声明端与调用
        // 点（可能属于另一个单独转换的工程，注册表为空）必须得到同一个名字。
        return sym != null && CompatCollectionConformance.IsRenamableRemove(sym, ctx)
            ? javaName + "_2"
            : javaName;
    }

    /// <summary>
    /// Implicit interface implementations keep the C# name even though the
    /// mapped Java interface renames the method (e.g. a synthesized tuple struct
    /// implementing <c>System.IEquatable&lt;T&gt;.Equals(T)</c> must declare
    /// <c>equalsTo(T)</c> to satisfy the mapped compat interface).
    /// </summary>
    private static string? TryMapImplicitInterfaceImplementation(
        string name, IMethodSymbol? sym, ConversionContext ctx)
    {
        if (sym?.ContainingType is not { } owner)
            return null;
        foreach (var iface in owner.AllInterfaces)
        {
            foreach (var interfaceMethod in iface.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (owner.FindImplementationForInterfaceMember(interfaceMethod) is not IMethodSymbol impl)
                    continue;
                if (!SymbolEqualityComparer.Default.Equals(impl, sym)
                    && !SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, sym.OriginalDefinition))
                {
                    continue;
                }
                var mapped = ctx.TypeMappings.MapMethod(iface.ToDisplayString(), name);
                if (!string.IsNullOrEmpty(mapped) && !mapped.Contains('.'))
                    return mapped;
            }
        }
        return null;
    }

    // ================== 显式接口实现：映射接口成员形状权威 ==================

    /// <summary>
    /// compat 收紧族接口（CSharpIList 等）的成员 Java 形状由 compat jar 权威，
    /// 与 C# 接口槽返回类型的直接映射不同：
    /// <list type="bullet">
    /// <item>removeAt 返回 Object：容纳协变的公开 RemoveAt 实现（真实链
    /// XmlAttributeCollection.RemoveAt→XmlAttribute；C# void 形状是硬冲突，
    /// compat 侧 default 化无法消除）；</item>
    /// <item>remove 返回 boolean：统一 ICollection&lt;T&gt;.Remove 的 bool 形状；
    /// 非泛型 System.Collections.IList.Remove 的 void 与之同擦除签名，硬冲突。</item>
    /// </list>
    /// 键 =（接口原始定义命名空间，接口简单名，成员名）：同擦除签名才会冲突，
    /// 无需参数维度。仅对显式实现生效（隐式公开成员保留 C# 形状以协变实现）。
    /// </summary>
    private static readonly Dictionary<(string Namespace, string Interface, string Member), string>
        CompatInterfaceMemberReturnTypes = new()
        {
            [("System.Collections.Generic", "IList", "RemoveAt")] = "Object",
            [("System.Collections", "IList", "RemoveAt")] = "Object",
            [("System.Collections.Generic", "IList", "Remove")] = "boolean",
            [("System.Collections.Generic", "ICollection", "Remove")] = "boolean",
            [("System.Collections", "IList", "Remove")] = "boolean",
        };

    /// <summary>
    /// 显式接口实现方法（桥）：Java 返回类型采用映射接口成员的形状——生成类
    /// implements 的就是映射后的 Java 接口，成员形状以接口为准（与
    /// <see cref="TryMapImplicitInterfaceImplementation"/> 的接口映射改名同理）。
    /// C# 侧显式实现与接口槽的返回类型恒相同，但映射后可能分叉：C# void 槽位
    /// 映射到 compat 接口的非 void 成员（CSharpIList.removeAt→Object、
    /// remove→boolean），void 实现是 javac 硬错（返回类型不兼容）。返回类型从
    /// void 变为非 void 时，纯抛出体无需处理（Java 允许任意返回类型的方法以
    /// throw 结尾），其余非终结体补默认返回语句防缺失返回/不可达编译错。
    /// </summary>
    private static void ApplyExplicitInterfaceMemberShape(
        JavaMethodDeclaration method, IMethodSymbol? sym, ConversionContext ctx)
    {
        if (sym == null
            || sym.ExplicitInterfaceImplementations.Length == 0
            || method.Modifiers.HasFlag(JavaModifiers.Abstract))
        {
            return;
        }

        // ExplicitInterfaceImplementations 给出的是替换了类型实参的构造槽符号。
        var slot = sym.ExplicitInterfaceImplementations.OfType<IMethodSymbol>().FirstOrDefault();
        if (slot == null || slot.ContainingType == null)
            return;

        string authoritative;
        var originalInterface = slot.ContainingType.OriginalDefinition;
        if (CompatInterfaceMemberReturnTypes.TryGetValue(
                (originalInterface.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                 originalInterface.Name, slot.Name), out var compatShape))
        {
            authoritative = compatShape;
        }
        else
        {
            authoritative = slot.ReturnsVoid ? "void" : ctx.MapType(slot.ReturnType);
        }

        if (authoritative == "void"
            || authoritative == method.ReturnType
            || authoritative == "__suppress__")
        {
            return;
        }

        method.ReturnType = authoritative;
        EnsureBodyTerminatesForNonVoid(method, authoritative, ctx);
    }

    /// <summary>
    /// 返回类型变为非 void 后保证方法体可编译：纯抛出体与已有终结语句
    ///（throw/return）不处理——尾补 return 反而是不可达语句编译错；
    /// 结构化体补默认返回语句节点，字符串块体追加默认返回行；
    /// 单表达式体由发射器自动渲染 <c>return expr;</c>，无需处理。
    /// </summary>
    private static void EnsureBodyTerminatesForNonVoid(
        JavaMethodDeclaration method, string returnType, ConversionContext ctx)
    {
        var defaultValue = DefaultJavaReturnValue(returnType);

        if (method.StructuredBody != null)
        {
            var statements = method.StructuredBody.Statements;
            if (statements.Count > 0 && IsTerminalStatement(statements[^1].ToString(string.Empty)))
                return;
            statements.Add(new Transformers.Statement.JavaStatementNode($"return {defaultValue};"));
            return;
        }

        if (method.Body == null || method.IsBodyExpression)
            return;

        if (method.Body.TrimStart().StartsWith("throw ", StringComparison.Ordinal))
            return;

        var lastLine = method.Body.Split('\n')
            .LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        if (lastLine != null && IsTerminalStatement(lastLine))
            return;

        method.Body = $"{method.Body}\nreturn {defaultValue};";
        ctx.Diagnostics.Warning(
            $"Explicit interface implementation '{method.Name}' adopts mapped interface return type "
            + $"'{returnType}'; appended a default return because the body has no terminal statement.");
    }

    private static bool IsTerminalStatement(string statement)
        => statement.StartsWith("throw ", StringComparison.Ordinal)
           || statement.StartsWith("return ", StringComparison.Ordinal);

    /// <summary>非 void 形状的默认返回值：基元取零值，其余（含 Object）取 null。</summary>
    private static string DefaultJavaReturnValue(string javaType) => javaType switch
    {
        "boolean" => "false",
        "int" or "short" or "byte" => "0",
        "long" => "0L",
        "double" => "0.0",
        "float" => "0.0f",
        "char" => "'\\0'",
        _ => "null",
    };

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

    /// <summary>
    /// Two C# overloads can collapse to the same Java erasure signature — e.g.
    /// <c>f(..., ref bool, ref bool)</c> vs <c>f(..., ref bool, ref string)</c> both
    /// become <c>f(..., ByRef, ByRef)</c>. Rename the colliding overload
    /// deterministically and publish the rename so call sites bind to it; otherwise
    /// the mechanical de-duplicator silently drops one overload.
    /// </summary>
    private static void TryRenameForErasureConflict(
        JavaMethodDeclaration method, IMethodSymbol? symbol, ConversionContext context)
    {
        if (symbol == null || context.CurrentType is not JavaClassDeclaration javaClass)
            return;
        // Explicit interface implementations are bridges: when an implicit member
        // already occupies the Java signature, the deduplicator drops the bridge —
        // renaming it would emit a call to a method that does not exist.
        if (symbol.ExplicitInterfaceImplementations.Length > 0)
            return;

        var signature = ErasedSignature(method.Parameters, symbol);
        var colliding = javaClass.Methods.FirstOrDefault(m =>
            m.Name == method.Name && ErasedSignature(m.Parameters, null) == signature);
        if (colliding == null)
            return;
        // Property accessors keep their bean name: the mechanical dedup drops the
        // duplicate user method and call sites bind to the accessor (the delegation
        // case is handled by the pre-scan, which registers an explicit rename).
        if (colliding.IsAutoGenerated)
            return;

        // Names reserved by overrides in this type must stay available so the
        // override keeps binding to its base member (Java requires identical names).
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in symbol.ContainingType.GetMembers().OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(member, symbol) || member.OverriddenMethod == null)
                continue;
            if (member.OverriddenMethod is { } baseMember)
            {
                var baseDeclaration = context.FindMethodDeclaration(baseMember);
                if (baseDeclaration != null)
                    reserved.Add(baseDeclaration.Name);
                else if (TryPredictErasureConflictName(baseMember, context, out var reservedName))
                    reserved.Add(reservedName);
            }
            else if (context.TryGetErasureConflictMethodName(member, out var reservedName2)
                || TryPredictErasureConflictName(member, context, out reservedName2))
            {
                reserved.Add(reservedName2);
            }
        }

        var suffix = 2;
        // A same-named override in this class owns the first conflict suffix; a
        // non-override sibling (e.g. ReflectionEmitMemberAccessor's private
        // helper) must start past it, otherwise the override cannot keep its
        // base-bound name.
        if (symbol.OverriddenMethod == null
            && symbol.ContainingType.GetMembers(symbol.Name).OfType<IMethodSymbol>()
                .Any(m => !SymbolEqualityComparer.Default.Equals(m, symbol) && m.OverriddenMethod != null))
        {
            suffix = 3;
        }
        while (javaClass.Methods.Any(m => m.Name == $"{method.Name}_{suffix}")
               || reserved.Contains($"{method.Name}_{suffix}"))
            suffix++;

        method.Name = $"{method.Name}_{suffix}";
        context.RegisterErasureConflictMethodName(symbol, method.Name);
    }

    private static string ErasedSignature(IEnumerable<JavaParameter> parameters, IMethodSymbol? symbol)
    {
        int index = 0;
        return string.Join(",", parameters.Select(p =>
        {
            // A generic method's type parameter erases to Object in Java; C#
            // overloads that differ only by T vs object collide at runtime.
            if (symbol != null && index < symbol.Parameters.Length
                && symbol.Parameters[index].Type is ITypeParameterSymbol)
            {
                index++;
                return "Object";
            }
            index++;
            var type = p.Type;
            var angle = type.IndexOf('<');
            return angle >= 0 ? type[..angle].TrimEnd() : type;
        }));
    }

    /// <summary>
    /// Predicts the erasure-conflict suffix exactly as
    /// <see cref="Type.ClassTransformer.PrepareErasureConflictRenames"/> assigns it:
    /// the type's members are grouped by (Java name, erased parameter signature) in
    /// declaration order, and every entry after the first of its own group becomes
    /// <c>name_2</c>, <c>name_3</c>… Suffixes are counted per group: a different
    /// signature that reuses the same Java name must not advance this group's
    /// counter (Java accepts the reuse because the erasures differ). Call sites
    /// converted in another project have an empty rename registry and rely on this
    /// prediction, so declaration and call site must derive the same suffix.
    /// </summary>
    internal static bool TryPredictErasureConflictName(
        IMethodSymbol method, ConversionContext context, out string javaName)
    {
        javaName = string.Empty;
        if (method.ExplicitInterfaceImplementations.Length > 0)
            return false;
        var containing = method.ContainingType;
        if (containing == null || !containing.Locations.Any(l => l.IsInSource))
            return false;
        if (containing.ContainingNamespace?.ToDisplayString() == "Cs2jByRef")
            return false;

        // Fast path: a rename can only exist when another member of the same type
        // maps to the same Java simple name. Checking that first keeps the common
        // case free of MapType side effects (which register imports).
        var baseName = GetJavaMethodName(method.Name, method, context);
        var hasSibling = containing.GetMembers().Any(member =>
            !SymbolEqualityComparer.Default.Equals(member, method)
            && member switch
            {
                IMethodSymbol sibling
                    when sibling.MethodKind is MethodKind.Ordinary
                        or MethodKind.ExplicitInterfaceImplementation =>
                    GetJavaMethodName(sibling.Name, sibling, context) == baseName,
                IPropertySymbol property when property.ExplicitInterfaceImplementations.Length == 0 =>
                    JavaNaming.GetPropertyGetterName(
                        ConversionContext.EscapeJavaKeyword(property.Name)) == baseName
                    || SetterName(property) == baseName,
                _ => false,
            });
        if (!hasSibling)
            return false;

        var entries = new List<(IMethodSymbol Symbol, string Name, string Signature,
            bool IsBridge, IPropertySymbol? AccessorOwner)>();
        foreach (var member in containing.GetMembers())
        {
            switch (member)
            {
                case IPropertySymbol property when property.ExplicitInterfaceImplementations.Length == 0:
                    if (property.GetMethod is { } getter)
                    {
                        entries.Add((getter,
                            JavaNaming.GetPropertyGetterName(
                                ConversionContext.EscapeJavaKeyword(property.Name)),
                            string.Empty, false, property));
                    }
                    if (property.SetMethod is { } setter)
                    {
                        entries.Add((setter, SetterName(property),
                            ErasedSignature(setter, context), false, property));
                    }
                    break;
                case IMethodSymbol memberMethod
                    when memberMethod.MethodKind is MethodKind.Ordinary
                        or MethodKind.ExplicitInterfaceImplementation:
                    entries.Add((memberMethod,
                        GetJavaMethodName(memberMethod.Name, memberMethod, context),
                        ErasedSignature(memberMethod, context),
                        memberMethod.ExplicitInterfaceImplementations.Length > 0, null));
                    break;
            }
        }

        foreach (var group in entries.GroupBy(e => (e.Name, e.Signature)).Where(g => g.Count() > 1))
        {
            var grouped = group.ToList();
            var accessorOwner = grouped.FirstOrDefault(e => e.AccessorOwner != null).AccessorOwner;
            var suffix = grouped.Any(e => e.Symbol.OverriddenMethod != null) ? 3 : 2;
            for (var i = 1; i < grouped.Count; i++)
            {
                var entry = grouped[i];
                if (entry.Symbol.OverriddenMethod != null || entry.IsBridge)
                    continue;
                // Declaration-side policy: a hand-written GetXxx/set-Xxx that only
                // forwards the property is dropped in favour of the bean accessor,
                // so the call site must keep the base name.
                if (accessorOwner != null && IsSimpleAccessorDuplicate(entry.Symbol, accessorOwner))
                    continue;
                var candidate = $"{group.Key.Name}_{suffix++}";
                if (SymbolEqualityComparer.Default.Equals(entry.Symbol, method)
                    || SymbolEqualityComparer.Default.Equals(
                        entry.Symbol.OriginalDefinition, method.OriginalDefinition))
                {
                    javaName = candidate;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Syntax-only twin of <c>ClassTransformer.IsSimpleAccessorRead/Assignment</c>:
    /// true when the method body is just <c>return X;</c> (or <c>this.X = value;</c>)
    /// for the property in the same erasure group.
    /// </summary>
    private static bool IsSimpleAccessorDuplicate(IMethodSymbol method, IPropertySymbol property)
    {
        var declaration = method.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        if (declaration == null)
            return false;

        if (method.Parameters.Length == 0)
        {
            var returned = declaration.Body is { Statements.Count: 1 } body
                && body.Statements[0] is ReturnStatementSyntax returnStatement
                    ? returnStatement.Expression
                    : declaration.ExpressionBody?.Expression;
            return returned != null && IsPropertyReference(returned, property.Name);
        }

        if (method.Parameters.Length != 1)
            return false;

        var assignment = declaration.Body is { Statements.Count: 1 } assignBody
            && assignBody.Statements[0] is ExpressionStatementSyntax statement
            && statement.Expression is AssignmentExpressionSyntax assign
                ? assign
                : declaration.ExpressionBody?.Expression as AssignmentExpressionSyntax;
        if (assignment == null || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            return false;
        return IsPropertyReference(assignment.Left, property.Name)
            && assignment.Right is IdentifierNameSyntax right
            && right.Identifier.Text == method.Parameters[0].Name;
    }

    private static bool IsPropertyReference(ExpressionSyntax expression, string propertyName) =>
        expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text == propertyName,
            MemberAccessExpressionSyntax
            {
                Expression: ThisExpressionSyntax,
                Name: IdentifierNameSyntax identifier
            } => identifier.Identifier.Text == propertyName,
            _ => false,
        };

    private static string SetterName(IPropertySymbol property)
    {
        var escaped = ConversionContext.EscapeJavaKeyword(property.Name);
        return "set" + (escaped.Length > 0
            ? char.ToUpperInvariant(escaped[0]) + escaped[1..]
            : escaped);
    }

    private static string ErasedSignature(IMethodSymbol method, ConversionContext context) =>
        string.Join(",", method.Parameters.Select(p =>
            // 泛型方法的类型参数在 Java 擦除成 Object；声明侧（带符号的重载）
            // 也按 Object 分组，这里必须一致，否则「泛型 TValue 参数 vs object
            // 参数」的同签名重载不会被预测为改名（真实链 STJ Serialize 入口）。
            p.Type is ITypeParameterSymbol ? "Object" : EraseJavaType(context.MapType(p.Type))));

    private static string EraseJavaType(string type)
    {
        var angle = type.IndexOf('<');
        return angle >= 0 ? type[..angle].TrimEnd() : type;
    }

    /// <summary>
    /// True when any parameter type (or the return type) maps to the "__suppress__"
    /// sentinel configured for serialization-only BCL types.
    /// </summary>
    private static bool HasSuppressedSignature(IMethodSymbol? symbol, ConversionContext ctx)
    {
        if (symbol == null)
            return false;

        if (symbol.ReturnsVoid == false
            && symbol.ReturnType is not null
            && ctx.MapType(symbol.ReturnType) == "__suppress__")
        {
            return true;
        }

        return symbol.Parameters.Any(p => ctx.MapType(p.Type) == "__suppress__");
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
