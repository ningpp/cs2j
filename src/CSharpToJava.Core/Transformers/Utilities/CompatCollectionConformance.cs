using Microsoft.CodeAnalysis;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Member;
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

    /// <summary>
    /// compat 集合实现类上必须改名的 C# <c>Remove</c>：参数恰好 1 个、返回类型既非
    /// bool 也非 void。这类成员与 compat 接口的 <c>boolean remove(Object)</c> 擦除后
    /// 同签名却返回类型不兼容，声明端统一发成 <c>remove_2</c>。
    /// <para>
    /// 判定只依赖符号本身（+ 类型映射配置），不依赖本项目 ConversionContext 的擦除
    /// 冲突改名注册表：调用点可能属于另一个被单独转换的工程（新 Context、空注册表，
    /// 真实链 dotnet-xml-document-tests 的 <c>target.remove(attr2)</c>），必须能自行
    /// 推导出与声明端一致的名字。改动本判定时必须与
    /// <see cref="MethodTransformer.GetJavaMethodName"/>（追加 "_2"）和
    /// <see cref="EnsureRemoveBridge"/>（补 boolean 桥）保持一致。
    /// </para>
    /// </summary>
    public static bool IsRenamableRemove(IMethodSymbol? method, ConversionContext context)
    {
        if (method is not { Name: "Remove", Parameters.Length: 1 })
            return false;
        // 显式接口实现（桥）保持映射接口成员形状，绝不改名。
        if (method.MethodKind != MethodKind.Ordinary
            || method.ExplicitInterfaceImplementations.Length > 0)
        {
            return false;
        }
        if (method.ReturnType.SpecialType
            is SpecialType.System_Boolean or SpecialType.System_Void)
        {
            return false;
        }
        // 只对源码声明生效：元数据类型（List<T>、compat 宿主等）由 compat 提供 Java
        // 成员，不能凭空派生 _2 后缀。
        var owner = method.ContainingType;
        if (owner == null || !owner.Locations.All(location => location.IsInSource))
            return false;
        return ImplementsCompatCollection(owner, context);
    }

    public static void Apply(JavaClassDeclaration javaClass, INamedTypeSymbol? symbol, ConversionContext context)
    {
        var branch = GetBranch(javaClass, symbol, context);
        if (branch == Branch.None)
            return;

        foreach (var method in javaClass.Methods.ToList())
        {
            if ((method.Modifiers & (JavaModifiers.Static | JavaModifiers.Abstract)) != 0)
                continue;

            // 非 public 的 C# 方法不是接口实现：private/protected 成员若按接口形状擦除
            // 形参，会与继承来的 compat public 默认方法同擦除签名却降低可见性，javac 报
            // “正在尝试分配更低的访问权限”（真实链 StringFunctions 的私有
            // Contains(XPathNodeIterator)）。保留强类型形参只是合法重载；接口桥由
            // 下面的 EnsureSyncMembers/EnsureRemoveBridge 以 public 补齐。
            if ((method.Modifiers & (JavaModifiers.Private | JavaModifiers.Protected)) != 0)
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
            AdaptBareReturns(method, "0");
            AddTrailingReturn(method, "return 0;");
            return;
        }

        // CSharpGenericIterable<T>.add(T)：元素类型即 C# 形参类型，只需 void→boolean。
        method.ReturnType = "boolean";
        AdaptBareReturns(method, "true");
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
        AdaptBareReturns(method, "null");
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
    /// java.util.Collection.remove(Object) 的实现，声明端已按确定性规则改名为 remove_2；
    /// 这里补发接口形状的 boolean 桥，语义取 C# “删除成功” 即返回非 null。
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

            // 其余擦除冲突改名仍来自本项目注册表；compat 集合 Remove 的 _2 后缀则由
            // 符号形状确定性推导（跨工程调用点用同一推导，不经过注册表）。
            string renamed;
            if (context.TryGetErasureConflictMethodName(member, out var registered))
                renamed = registered;
            else if (IsRenamableRemove(member, context))
                renamed = MethodTransformer.GetJavaMethodName(member.Name, member, context);
            else
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

    /// <summary>
    /// void → 非 void 适配：C# 方法体里早退的裸 <c>return;</c> 在 Java 非 void 方法中是
    /// “缺少返回值”。把返回类型改成 int/boolean/Object 时，同步把裸 return 改写为带默认
    /// 值的 return。方法体可能来自两条路径：结构化语句（<see cref="JavaMethodBody"/>）或
    /// 已渲染的原始字符串（if/for 等由语句转换器整体生成为多行文本），两条都要覆盖。
    /// </summary>
    private static void AdaptBareReturns(JavaMethodDeclaration method, string returnExpression)
    {
        if (method.StructuredBody != null)
        {
            RewriteBareReturnsInStatements(method.StructuredBody.Statements, returnExpression);
            return;
        }

        if (!method.IsBodyExpression && method.Body != null)
            method.Body = RewriteBareReturnsInText(method.Body, returnExpression);
    }

    private static void RewriteBareReturnsInStatements(
        List<JavaStatement> statements, string returnExpression)
    {
        for (var i = 0; i < statements.Count; i++)
            statements[i] = RewriteBareReturnsInStatement(statements[i], returnExpression);
    }

    /// <summary>
    /// 递归处理结构化语句里的裸 return；刻意不进入表达式，避免误改 lambda 的 void 返回。
    /// </summary>
    private static JavaStatement RewriteBareReturnsInStatement(
        JavaStatement statement, string returnExpression)
    {
        switch (statement)
        {
            case JavaReturnStatement { Expression: null } bare:
                bare.Expression = new JavaRawExpression(returnExpression);
                return bare;
            case JavaRawStatement raw:
                raw.Code = RewriteBareReturnsInText(raw.Code, returnExpression);
                return raw;
            case JavaBlockStatement block:
                RewriteBareReturnsInStatements(block.Statements, returnExpression);
                return block;
            case JavaIfStatement ifStatement:
                ifStatement.ThenBody =
                    RewriteBareReturnsInStatement(ifStatement.ThenBody, returnExpression);
                if (ifStatement.ElseBody != null)
                    ifStatement.ElseBody =
                        RewriteBareReturnsInStatement(ifStatement.ElseBody, returnExpression);
                return ifStatement;
            case JavaForEachStatement forEach:
                forEach.Body = RewriteBareReturnsInStatement(forEach.Body, returnExpression);
                return forEach;
            case JavaForStatement forStatement:
                forStatement.Body = RewriteBareReturnsInStatement(forStatement.Body, returnExpression);
                return forStatement;
            case JavaWhileStatement whileStatement:
                whileStatement.Body = RewriteBareReturnsInStatement(whileStatement.Body, returnExpression);
                return whileStatement;
            case JavaDoWhileStatement doWhile:
                doWhile.Body = RewriteBareReturnsInStatement(doWhile.Body, returnExpression);
                return doWhile;
            case JavaTryCatchStatement tryCatch:
                RewriteBareReturnsInStatements(tryCatch.TryBody.Statements, returnExpression);
                foreach (var catchClause in tryCatch.CatchClauses)
                    RewriteBareReturnsInStatements(catchClause.Body.Statements, returnExpression);
                if (tryCatch.FinallyBody != null)
                    RewriteBareReturnsInStatements(tryCatch.FinallyBody.Statements, returnExpression);
                return tryCatch;
            case JavaSwitchStatement switchStatement:
                foreach (var section in switchStatement.Sections)
                    RewriteBareReturnsInStatements(section.Statements, returnExpression);
                return switchStatement;
            default:
                return statement;
        }
    }

    /// <summary>
    /// 保守的行级文本改写：只把独占一行（可带缩进/行尾注释）的裸 <c>return;</c> 换成
    /// <c>return &lt;expr&gt;;</c>，不触碰 <c>return &lt;expr&gt;;</c> 或 <c>returnValue</c>
    /// 之类的标识符。生成语句文本按“一行一条语句”组织，该粒度足以覆盖早退 return。
    /// 保留原换行风格（Split('\n') 后把行尾 '\r' 原样接回）。
    /// </summary>
    private static string RewriteBareReturnsInText(string code, string returnExpression)
    {
        if (code.IndexOf("return", StringComparison.Ordinal) < 0)
            return code;

        var lines = code.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var carriageReturn = line.EndsWith('\r') ? "\r" : string.Empty;
            var text = carriageReturn.Length == 0 ? line : line[..^1];
            if (!TryRewriteBareReturnLine(text, returnExpression, out var rewritten))
                continue;
            lines[i] = rewritten + carriageReturn;
            changed = true;
        }

        return changed ? string.Join("\n", lines) : code;
    }

    private static bool TryRewriteBareReturnLine(
        string line, string returnExpression, out string rewritten)
    {
        rewritten = line;

        var indent = 0;
        while (indent < line.Length && char.IsWhiteSpace(line[indent]))
            indent++;
        if (!line.AsSpan(indent).StartsWith("return", StringComparison.Ordinal))
            return false;

        var afterKeyword = indent + "return".Length;
        if (afterKeyword < line.Length
            && !char.IsWhiteSpace(line[afterKeyword])
            && line[afterKeyword] != ';')
        {
            return false; // returnValue / returns 等标识符，不是 return 语句
        }

        var semicolon = afterKeyword;
        while (semicolon < line.Length && char.IsWhiteSpace(line[semicolon]))
            semicolon++;
        if (semicolon >= line.Length || line[semicolon] != ';')
            return false; // return <expr>;

        var trailing = line[(semicolon + 1)..];
        var trailingContent = trailing.TrimStart();
        // 行尾只允许空白或行注释；其它内容（如内联 lambda 的 "});"）保持原样。
        if (trailingContent.Length > 0
            && !trailingContent.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        rewritten = line[..indent] + "return " + returnExpression + ";" + trailing;
        return true;
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
