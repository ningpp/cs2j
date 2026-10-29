using Microsoft.CodeAnalysis;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Statement;

namespace CSharpToJava.Core.Transformers.Utilities;

/// <summary>
/// 生成的集合类必须满足 compat 集合接口的 Java 成员形状。C# 成员按自身声明直译后
/// 往往与映射接口成员“同擦除签名却互不覆盖”：<c>Set&lt;T&gt; : ICollection&lt;T&gt;</c>
/// 的 <c>void Add(T)</c> 对 <c>CSharpIList.add(Object)</c>（int）、<c>Contains(T)</c>
/// 对 <c>contains(Object)</c>、<c>IEnumerator&lt;T&gt; GetEnumerator()</c> 对
/// <c>CSharpCollection.iterator()</c>（元素擦除为 Object），javac 报“名称冲突 /
/// 未覆盖抽象方法”。本后处理按接口形状统一改写实现：形参擦除为 Object（方法体
/// 首部补回原类型强转）、返回类型对齐接口（C# void Add → int，void RemoveAt →
/// Object），并补齐 CSharpCollection 要求的 SyncRoot/IsSynchronized 成员。
/// </summary>
internal static class CompatCollectionConformance
{
    /// <summary>compat 集合分支：CSharpIList/CSharpCollection（元素擦除为 Object）或 CSharpGenericIterable&lt;T&gt;。</summary>
    private enum Branch
    {
        None,
        Collection,
        Iterable,
    }

    /// <summary>C# 类型是否实现任一 compat 集合接口分支（供擦除冲突预扫描判定 Remove 改名）。</summary>
    public static bool ImplementsCompatCollection(INamedTypeSymbol? type, ConversionContext context)
        => GetBranch(null, type, context) != Branch.None;

    public static void Apply(JavaClassDeclaration javaClass, INamedTypeSymbol? symbol, ConversionContext context)
    {
        var branch = GetBranch(javaClass, symbol, context);
        if (branch == Branch.None)
            return;

        foreach (var method in javaClass.Methods.ToList())
        {
            if ((method.Modifiers & (JavaModifiers.Static | JavaModifiers.Abstract)) != 0)
                continue;

            switch (method.Name)
            {
                case "add":
                    NormalizeAdd(method, branch, javaClass);
                    break;
                case "contains":
                case "remove":
                    NormalizeObjectPredicate(method, javaClass);
                    break;
                case "removeAt":
                    NormalizeRemoveAt(method);
                    break;
                case "iterator":
                    if (branch == Branch.Collection)
                        NormalizeIterator(method, context);
                    break;
            }
        }

        if (branch == Branch.Collection)
            EnsureSyncMembers(javaClass);
        EnsureRemoveBridge(javaClass, symbol, context);
    }

    // ── 分支判定 ────────────────────────────────────────────────────────────────

    private static Branch GetBranch(
        JavaClassDeclaration? javaClass, INamedTypeSymbol? symbol, ConversionContext context)
    {
        // 生成类实际 implements 的 compat 接口最权威（类发射按擦除去重/收窄）；
        // 基类继承来的分支在 ImplementedTypes 里看不到，回落 C# 接口图。
        if (javaClass != null)
        {
            var declaredBranch = Classify(javaClass.ImplementedTypes);
            if (declaredBranch != Branch.None)
                return declaredBranch;
        }

        if (symbol == null)
            return Branch.None;

        // 只按直接声明的接口判定：ICollection<T> 隐含的 IEnumerable 属于同一分支，
        // 混进 AllInterfaces 会误判成 CSharpCollection + CSharpGenericIterable 菱形。
        var directBranch = Classify(symbol.Interfaces
            .Where(i => i.Name is "ICollection" or "IList" or "IEnumerable")
            .Select(i => context.MapType(i)));
        if (directBranch != Branch.None)
            return directBranch;

        return Classify(symbol.AllInterfaces
            .Where(i => i.Name is "ICollection" or "IList" or "IEnumerable")
            .Select(i => context.MapType(i)));
    }

    /// <summary>
    /// 由 java 接口引用集合判定分支。两条无关继承链同时存在（CSharpCollection 与
    /// java.util.Collection 菱形）时集合表面成员互相冲突、形状无单一权威，返回 None
    /// 保持原样，交由类发射收窄处理。
    /// </summary>
    private static Branch Classify(IEnumerable<string> mappedTypes)
    {
        var hasCollection = false;
        var hasIterable = false;
        foreach (var mapped in mappedTypes)
        {
            switch (ErasureKey(mapped))
            {
                case "CSharpIList":
                case "CSharpCollection":
                    hasCollection = true;
                    break;
                case "CSharpGenericIterable":
                    hasIterable = true;
                    break;
            }
        }

        if (hasCollection && hasIterable)
            return Branch.None;
        if (hasCollection)
            return Branch.Collection;
        return hasIterable ? Branch.Iterable : Branch.None;
    }

    /// <summary>接口引用的擦除简单名（去掉泛型实参与包前缀）。</summary>
    private static string ErasureKey(string mappedType)
    {
        var body = mappedType;
        var lt = body.IndexOf('<');
        if (lt >= 0) body = body[..lt];
        var dot = body.LastIndexOf('.');
        return dot >= 0 ? body[(dot + 1)..] : body;
    }

    // ── 成员形状对齐 ────────────────────────────────────────────────────────────

    /// <summary>
    /// C# <c>void Add(T)</c>：CSharpIList 分支要求 <c>int add(Object)</c>（C# IList.Add
    /// 返回插入位置），CSharpGenericIterable 分支要求 <c>boolean add(T)</c>。返回类型已是
    /// 接口形状的成员保持不动（它们要么已正确覆盖，要么只是同名的其它重载）。
    /// </summary>
    private static void NormalizeAdd(JavaMethodDeclaration method, Branch branch, JavaClassDeclaration javaClass)
    {
        if (method.Parameters.Count != 1 || method.ReturnType != "void")
            return;

        if (branch == Branch.Collection)
        {
            if (HasObjectParameterOverload(javaClass, method.Name))
                return;
            EraseParameter(method, 0);
            method.ReturnType = "int";
            AddTrailingReturn(method, "return 0;");
            return;
        }

        // CSharpGenericIterable<T>.add(T)：元素类型即 C# 形参类型，只需 void→boolean。
        method.ReturnType = "boolean";
        AddTrailingReturn(method, "return true;");
    }

    /// <summary>
    /// C# <c>bool Contains(T)</c>/<c>bool Remove(T)</c> → <c>boolean (contains|remove)(Object)</c>：
    /// compat 接口成员是 Object 形参（元素类型擦除），带类型形参的实现与接口成员同擦除
    /// 签名却互不覆盖，concrete 元素的实现则落回接口 default（抛 UnsupportedOperationException）。
    /// </summary>
    private static void NormalizeObjectPredicate(JavaMethodDeclaration method, JavaClassDeclaration javaClass)
    {
        if (method.Parameters.Count != 1 || method.ReturnType != "boolean")
            return;
        if (method.Parameters[0].Type == "Object")
            return;
        if (HasObjectParameterOverload(javaClass, method.Name))
            return;
        EraseParameter(method, 0);
    }

    /// <summary>
    /// C# <c>void RemoveAt(int)</c> → <c>Object removeAt(int)</c>：compat 成员的返回类型是
    /// Object（容纳协变的 C# RemoveAt 返回值），void 实现是 javac 硬错。
    /// </summary>
    private static void NormalizeRemoveAt(JavaMethodDeclaration method)
    {
        if (method.Parameters.Count != 1 || method.Parameters[0].Type != "int")
            return;
        if (method.ReturnType != "void")
            return;
        method.ReturnType = "Object";
        AddTrailingReturn(method, "return null;");
    }

    /// <summary>
    /// CSharpCollection 分支的 <c>iterator()</c> 元素类型擦除为 Object（CSharpIterable&lt;Object&gt;）：
    /// 声明 <c>CSharpGenericEnumerator&lt;T&gt;</c> 无法实现接口。返回类型收为 raw，并把方法体
    /// 的返回值经 <c>CSharpGenericEnumerator.from(...)</c> 适配（对已是枚举器的值原样透传）。
    /// </summary>
    private static void NormalizeIterator(JavaMethodDeclaration method, ConversionContext context)
    {
        if (method.Parameters.Count != 0)
            return;
        var returnType = method.ReturnType;
        if (returnType is "CSharpGenericEnumerator" or "CSharpGenericEnumerator<Object>")
            return;
        if (!returnType.StartsWith("CSharpGenericEnumerator<", StringComparison.Ordinal))
            return;

        if (WrapIteratorReturn(method))
        {
            method.ReturnType = "CSharpGenericEnumerator";
            context.TypeMapper.AddImportPublic("io.github.ningpp.compat.CSharpGenericEnumerator");
        }
    }

    /// <summary>CSharpCollection 要求 Count/CopyTo/IsSynchronized/SyncRoot；C# 成员名不同或缺失时补桥。</summary>
    private static void EnsureSyncMembers(JavaClassDeclaration javaClass)
    {
        if (!javaClass.Methods.Any(m => m.Name == "getSyncRoot" && m.Parameters.Count == 0))
            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Name = "getSyncRoot",
                Modifiers = JavaModifiers.Public,
                ReturnType = "Object",
                IsBodyExpression = true,
                Body = "this",
            });

        if (!javaClass.Methods.Any(m => m.Name == "getIsSynchronized" && m.Parameters.Count == 0))
            javaClass.Methods.Add(new JavaMethodDeclaration
            {
                Name = "getIsSynchronized",
                Modifiers = JavaModifiers.Public,
                ReturnType = "boolean",
                IsBodyExpression = true,
                Body = "false",
            });
    }

    /// <summary>
    /// C# <c>bool Remove(T)</c> 之外形状（如 RbTree&lt;T&gt;.Remove 返回被删节点）无法充当
    /// java.util.Collection.remove(Object) 的实现，预扫描已把它改名为 remove_N；这里补发
    /// 接口形状的 boolean 桥，语义取 C# “删除成功” 即返回非 null。
    /// </summary>
    private static void EnsureRemoveBridge(
        JavaClassDeclaration javaClass, INamedTypeSymbol symbol, ConversionContext context)
    {
        if (javaClass.Methods.Any(m => m.Name == "remove" && m.Parameters.Count == 1))
            return;

        foreach (var member in symbol.GetMembers("Remove").OfType<IMethodSymbol>())
        {
            if (member.Parameters.Length != 1 || member.ReturnType.IsValueType)
                continue;
            if (!context.TryGetErasureConflictMethodName(member, out var renamed))
                continue;

            var castType = context.MapType(member.Parameters[0].Type);
            var bridge = new JavaMethodDeclaration
            {
                Name = "remove",
                Modifiers = JavaModifiers.Public,
                ReturnType = "boolean",
                Body = $"{renamed}(({castType}) __cs2jValue) != null",
                IsBodyExpression = true,
            };
            bridge.Parameters.Add(new JavaParameter("Object", "__cs2jValue"));
            if (castType != "Object")
                bridge.Annotations.Add(new JavaAnnotation("SuppressWarnings(\"unchecked\")"));
            javaClass.Methods.Add(bridge);
            return;
        }
    }

    // ── 方法体改写 ──────────────────────────────────────────────────────────────

    private static bool HasObjectParameterOverload(JavaClassDeclaration javaClass, string name)
        => javaClass.Methods.Any(m => m.Name == name
                                      && m.Parameters.Count == 1
                                      && m.Parameters[0].Type == "Object");

    /// <summary>
    /// 把形参擦除为 Object：形参改名为 <c>__cs2jValue</c>，方法体首部补回原类型的
    /// 强制转换，使方法体对原形参名的引用保持类型正确（C# 无 unchecked 概念）。
    /// </summary>
    private static void EraseParameter(JavaMethodDeclaration method, int index)
    {
        var parameter = method.Parameters[index];
        if (parameter.Type == "Object")
            return;

        var castType = parameter.Type;
        var originalName = parameter.Name;
        var erasedName = UniqueParameterName(method);
        parameter.Type = "Object";
        parameter.Name = erasedName;

        if (castType != "Object")
            method.Annotations.Add(new JavaAnnotation("SuppressWarnings(\"unchecked\")"));

        PrependStatement(method, $"{castType} {originalName} = ({castType}) {erasedName};");
    }

    private static string UniqueParameterName(JavaMethodDeclaration method)
    {
        var name = "__cs2jValue";
        var suffix = 2;
        while (method.Parameters.Any(p => p.Name == name))
            name = $"__cs2jValue{suffix++}";
        return name;
    }

    private static void PrependStatement(JavaMethodDeclaration method, string statement)
    {
        if (method.StructuredBody != null)
        {
            method.StructuredBody.Statements.Insert(0, new JavaStatementNode(statement));
            return;
        }

        if (method.Body == null)
        {
            method.Body = statement;
            return;
        }

        if (method.IsBodyExpression)
        {
            // 表达式体按语句 + 返回值展开，前置语句必须先于表达式求值。
            var expression = method.ReturnType == "void"
                ? $"{method.Body.TrimEnd(';')};"
                : $"return {method.Body.TrimEnd(';')};";
            method.Body = $"{statement}\n{expression}";
            method.IsBodyExpression = false;
            return;
        }

        method.Body = statement + "\n" + method.Body;
    }

    private static void AddTrailingReturn(JavaMethodDeclaration method, string returnStatement)
    {
        if (method.IsBodyExpression && method.Body != null)
        {
            method.Body = $"{method.Body.TrimEnd(';')};\n{returnStatement}";
            method.IsBodyExpression = false;
            return;
        }

        if (method.StructuredBody != null)
        {
            if (!EndsWithTerminalStatement(method.StructuredBody))
                method.StructuredBody.Statements.Add(new JavaStatementNode(returnStatement));
            return;
        }

        if (method.Body == null)
        {
            method.Body = returnStatement;
            return;
        }

        var lastLine = method.Body.Split('\n')
            .LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        if (lastLine != null
            && (lastLine.StartsWith("throw ", StringComparison.Ordinal)
                || lastLine.StartsWith("return", StringComparison.Ordinal)))
        {
            return;
        }

        method.Body = $"{method.Body}\n{returnStatement}";
    }

    private static bool EndsWithTerminalStatement(JavaMethodBody body)
    {
        if (body.Statements.Count == 0)
            return false;
        return body.Statements[^1] switch
        {
            JavaReturnStatement or JavaThrowStatement => true,
            JavaRawStatement raw => IsTerminalCode(raw.Code),
            _ => false,
        };
    }

    private static bool IsTerminalCode(string code)
    {
        var trimmed = code.TrimStart();
        return trimmed.StartsWith("throw ", StringComparison.Ordinal)
               || trimmed.StartsWith("return", StringComparison.Ordinal);
    }

    /// <summary>
    /// 把 iterator() 的返回值包一层 <c>CSharpGenericEnumerator.from(...)</c> 收窄成枚举器。
    /// 只处理可安全定位返回表达式的形状（单 return 的结构化体 / 单行 return 字符串体 /
    /// 表达式体）；其它形状保持原样，避免破坏已渲染的方法体。
    /// </summary>
    private static bool WrapIteratorReturn(JavaMethodDeclaration method)
    {
        const string adapter = "CSharpGenericEnumerator.from";

        if (method.IsBodyExpression && method.Body != null)
        {
            method.Body = $"{adapter}({method.Body.TrimEnd(';')})";
            return true;
        }

        if (method.StructuredBody is { Statements.Count: 1 } structured)
        {
            switch (structured.Statements[0])
            {
                case JavaReturnStatement { Expression: { } expression }:
                    expression = new JavaRawExpression($"{adapter}({expression.ToInlineString()})");
                    structured.Statements[0] = new JavaReturnStatement { Expression = expression };
                    return true;
                case JavaRawStatement raw when TryWrapReturnLine(raw.Code, adapter, out var wrapped):
                    raw.Code = wrapped;
                    return true;
                default:
                    return false;
            }
        }

        if (method.StructuredBody == null && !string.IsNullOrEmpty(method.Body))
        {
            if (!TryWrapReturnLine(method.Body, adapter, out var wrapped))
                return false;
            method.Body = wrapped;
            return true;
        }

        return false;
    }

    /// <summary>把单行 <c>return X;</c> 改写为 <c>return adapter(X);</c>。</summary>
    private static bool TryWrapReturnLine(string code, string adapter, out string wrapped)
    {
        wrapped = string.Empty;
        var trimmed = code.Trim();
        if (!trimmed.StartsWith("return ", StringComparison.Ordinal)
            || !trimmed.EndsWith(";", StringComparison.Ordinal)
            || trimmed.Contains('\n'))
        {
            return false;
        }

        var expression = trimmed["return ".Length..^1].Trim();
        if (expression.Length == 0)
            return false;
        wrapped = $"return {adapter}({expression});";
        return true;
    }
}
