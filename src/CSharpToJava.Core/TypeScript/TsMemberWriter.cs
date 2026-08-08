using System.Text;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript.Rewriters;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 成员发射器：字段/方法/构造器/属性发射为 TS（spec §2.5 成员部分）。
/// </summary>
public sealed class TsMemberWriter
{
    private readonly TsStatementWriter _stmtWriter;
    private readonly TsExpressionWriter _exprWriter;   // 用于 Java→TS 类型映射
    private readonly string _indent;

    /// <summary>Round 16：类成员上下文（裸实例/静态成员名补 this./类名. 前缀）。</summary>
    public TsMemberContext? MemberContext { get; set; }

    public TsMemberWriter(TsStatementWriter stmtWriter, TsExpressionWriter exprWriter, string indent = "    ")
    {
        _stmtWriter = stmtWriter;
        _exprWriter = exprWriter;
        _indent = indent;
    }

    /// <summary>Round 16：设置三个 writer 共享的类成员上下文（EmitClass 每个类调用一次）。</summary>
    public void SetMemberContext(TsMemberContext? memberContext)
    {
        MemberContext = memberContext;
        _stmtWriter.MemberContext = memberContext;
        _exprWriter.MemberContext = memberContext;
    }

    /// <summary>
    /// Java→TS 类型映射转发（类/接口成员发射共用，保持映射一致）。
    /// </summary>
    public string MapType(string javaOrCsharpType) => _exprWriter.MapType(javaOrCsharpType);

    /// <summary>
    /// 非可空上下文映射（Task 20）：泛型实参/继承约束等位置的装箱名
    /// （Java 泛型装箱产物）不追加 `| null`，用于 MapHeritageType/FormatTypeParameters。
    /// </summary>
    public string MapTypeNonNull(string javaOrCsharpType) => _exprWriter.MapTypeNonNull(javaOrCsharpType);

    /// <summary>异常名映射转发（静态初始化块归一化用）。</summary>
    public string? MapExceptionName(string name) => _exprWriter.MapExceptionName(name);

    public string EmitField(JavaFieldDeclaration field, int indentLevel, System.Collections.Generic.IReadOnlySet<string>? typeParameters = null)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // 修饰符
        if ((field.Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        else if ((field.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        else if ((field.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        if ((field.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((field.Modifiers & JavaModifiers.Final) != 0) sb.Append("readonly ");

        // IR 字段类型为 Java 风味（int/String/...），先经映射转 TS
        var tsType = _exprWriter.MapType(field.Type);
        var name = TsNaming.SafeIdentifier(field.Name);
        var hasInitializer = !string.IsNullOrEmpty(field.Initializer);

        // Task 27：record 位置分量字段由主构造器逐字段赋值，发射省略默认零值初始化器
        // （无 `= null` 也就无需 `T | null` 宽化，getter `T` 返回与字段类型一致）
        if (field.IsRecordComponent)
        {
            sb.Append($"{name}: {tsType};");
            return sb.ToString();
        }

        // 无初始化器补默认零值（spec §2.5：tsc strictPropertyInitialization 要求）。
        // strictNullChecks 下 null 不可赋给非空类型（TS2322）：实际发射 `= null` 时
        // 类型放宽为 `T | null`（含无初始化器默认零值为 null、以及初始化器为 null
        // 字面量两种形态；Task 13 扩展后者使 `int? x = null` → `number | null` 成立，
        // 与既有约定一致；可空体系深度语义属 M2 Task 20）。
        var emitsNull = hasInitializer
            ? string.Equals(field.Initializer, "null", StringComparison.Ordinal)
            : DefaultValueForType(tsType) == "null";
        var emittedType = tsType;
        // Task 41：泛型类类型参数（如 Box<T> 的 T _value）不可宽化为 T | null——
        // getter 返回 T 时 tsc 报 TS2322；无初始化器默认值改发射 null!（非空断言，
        // 与 strictPropertyInitialization 兼容），类型保持 T。
        var isTypeParameter = typeParameters != null && typeParameters.Contains(tsType);
        // Round 19：无初始化器引用类型字段（C# 非可空上下文中由构造器赋值，如
        // `readonly List<ICurve> segs;`）不再宽化 `T | null`——否则每个解引用点
        // TS2531/TS18047、属性 getter 内部 `return this.field` TS2322（MSAGL 全库
        // ~3700 条）。缺省零值改发射 null!（非空断言），类型保持 T，与上方
        // 类型参数路径同一惯例。显式 `= null` 初始化器（C# 真实可空）仍宽化。
        // any/null/undefined/void 吸收 null，无需非空断言，保持裸 `= null`。
        var isDefaultedNullField = !hasInitializer && DefaultValueForType(tsType) == "null"
            && tsType is not ("null" or "any" or "undefined" or "void");
        if (emitsNull && !isTypeParameter && !isDefaultedNullField
            && !tsType.Contains("| null") && !tsType.Contains("null |")
            && tsType is not ("null" or "any" or "undefined" or "void"))
        {
            // 函数类型（委托）后缀 | null 会并入返回类型（(a) => string | null），
            // 外层并集需显式括号 ((a) => string) | null（Task 27 委托）
            emittedType = tsType.Contains("=>") ? $"({tsType}) | null" : $"{tsType} | null";
        }
        sb.Append($"{name}: {emittedType}");

        if (hasInitializer)
        {
            // 初始化器为 Java 风味字面量（FieldTransformer.RenderConstantValue 产出
            // 100L/1.5f/0xFFL 等），需归一化为合法 TS 字面量再发射
            sb.Append($" = {NormalizeLiteral(field.Initializer, MemberContext)}");
        }
        else
        {
            // 类型参数/无初始化器引用类型字段用 null!（非空断言）保持 T；
            // 其余类型沿用默认零值
            sb.Append(isTypeParameter || isDefaultedNullField ? " = null!" : $" = {DefaultValueForType(tsType)}");
        }

        sb.Append(';');
        return sb.ToString();
    }

    public string EmitMethod(JavaMethodDeclaration method, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        if ((method.Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        else if ((method.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        else if ((method.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        if ((method.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((method.Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");

        // Task 27 async/await：async 方法经 MethodTransformer 标记 IsAsync，发射 `async ` 前缀
        if (method.IsAsync)
        {
            sb.Append("async ");
        }
        var name = TsNaming.SafeIdentifier(method.Name);
        // Task 27 yield→generator：yield 方法经 TsYieldRewriter 标记 IsGenerator，发射 `*name(...)`
        if (method.IsGenerator)
        {
            sb.Append('*');
        }
        sb.Append($"{name}{TsTypeWriter.FormatTypeParameters(method.TypeParameters, MapType)}(");

        // 参数/返回值类型需经 Java→TS 映射
        var parms = method.Parameters.Select(p =>
        {
            var pName = TsNaming.SafeIdentifier(p.Name);
            return $"{pName}: {_exprWriter.MapType(p.Type)}";
        });
        sb.Append(string.Join(", ", parms));

        sb.Append($"): {_exprWriter.MapType(method.ReturnType)}");

        AppendMethodBody(sb, method, ind, indentLevel);

        return sb.ToString();
    }

    /// <summary>
    /// Round 22：C# IEnumerable&lt;T&gt; 实现类经生成端产 Java 风味 `iterator()` 桥接，
    /// TS 目标缺 `[Symbol.iterator]()`——for-of（TS2488）、Iterable&lt;T&gt; 结构化实参
    /// （TS2741）、implements Iterable&lt;T&gt;（TS2420）三族同根。含 public 实例
    /// iterator() 的类补发 `[Symbol.iterator](): IterableIterator&lt;T&gt;`：
    /// 返回 IterableIterator（yield→generator 形态）时直接委托；moveNext()/getCurrent()
    /// 风味（CSharpGenericEnumerator）用适配器对象包装（TS 无 Java 枚举器协议）。
    /// </summary>
    public string? EmitSymbolIterator(JavaClassDeclaration cls, int indentLevel)
    {
        var iteratorMethod = cls.Methods.FirstOrDefault(m =>
            m.Name == "iterator"
            && m.Parameters.Count == 0
            && (m.Modifiers & JavaModifiers.Static) == 0);
        if (iteratorMethod == null) return null;

        var elementType = ExtractIteratorElementType(iteratorMethod.ReturnType, cls);
        var mapped = _exprWriter.MapTypeNonNull(elementType);
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        sb.Append($"[Symbol.iterator](): IterableIterator<{mapped}> {{").AppendLine();
        if (iteratorMethod.ReturnType.StartsWith("IterableIterator", StringComparison.Ordinal))
        {
            // yield→generator 形态：实际元素类型可能比类契约（implements Iterable<T>）
            // 更宽（IterableIterator<unknown>，TsYieldRewriter 元素类型丢失）——按类契约
            // 断言（C# IEnumerable<T> 契约），避免 IterableIterator<unknown> 与 T 失配
            // TS2322 与消费端元素退化为 unknown。
            sb.Append(ind).Append(_indent).AppendLine($"return this.iterator() as IterableIterator<{mapped}>;");
        }
        else
        {
            sb.Append(ind).Append(_indent).AppendLine("const __it = this.iterator();");
            sb.Append(ind).Append(_indent).AppendLine("return {");
            sb.Append(ind).Append(_indent).Append(_indent).AppendLine($"next(): IteratorResult<{mapped}> {{");
            sb.Append(ind).Append(_indent).Append(_indent).Append(_indent)
              .AppendLine($"if (__it.moveNext()) {{ return {{ value: __it.getCurrent() as {mapped}, done: false }}; }}");
            sb.Append(ind).Append(_indent).Append(_indent).Append(_indent)
              .AppendLine("return { done: true, value: undefined };");
            sb.Append(ind).Append(_indent).Append(_indent).AppendLine("},");
            sb.Append(ind).Append(_indent).Append(_indent).AppendLine("[Symbol.iterator]() { return this; }");
            sb.Append(ind).Append(_indent).AppendLine("};");
        }
        sb.Append(ind).Append('}');
        return sb.ToString();
    }

    /// <summary>[Symbol.iterator] 元素类型：优先取 implements 上 Iterable 类接口实参
    /// （C# IEnumerable&lt;T&gt; 的元素类型，非空）；回退 iterator() 返回类型泛型实参
    /// （可能带 Java 风味可空修饰）；再回退 any（与 Java Object→any 映射一致）。</summary>
    private static string ExtractIteratorElementType(string returnType, JavaClassDeclaration cls)
    {
        foreach (var iface in cls.ImplementedTypes)
        {
            var lt2 = iface.IndexOf('<');
            if (lt2 <= 0 || !iface.EndsWith(">", StringComparison.Ordinal)) continue;
            var baseName = iface.Substring(0, lt2);
            if (baseName is "Iterable" or "CSharpICollection" or "CSharpGenericIList" or "CSharpGenericIterable" or "CSharpCollection")
            {
                return iface.Substring(lt2 + 1, iface.Length - lt2 - 2).Trim();
            }
        }

        var lt = returnType.IndexOf('<');
        if (lt >= 0 && returnType.EndsWith(">", StringComparison.Ordinal))
        {
            return returnType.Substring(lt + 1, returnType.Length - lt - 2).Trim();
        }

        return "any";
    }

    /// <summary>
    /// Round 23：CSharpGenericEnumerator&lt;T&gt; implements 契约补发 getCurrent() 桥接。
    /// C# IEnumerator&lt;T&gt; 实现类的 Current 属性经属性重组发射为 `get Current()`（IR
    /// 方法 getCurrent + Getter role），而 global.d.ts 的 CSharpGenericEnumerator 接口
    /// 契约要求 Java 风味 getCurrent() 方法（TS2304 修复后 TS2420 缺口）。本钩子对
    /// 仅有 Current getter、无 getCurrent() 方法的枚举器类补发
    /// `getCurrent(): T { return this.Current; }`（TS 目标专用；Java 目标零接触）。
    /// </summary>
    public string? EmitGetCurrentBridge(
        JavaClassDeclaration cls,
        IReadOnlyDictionary<JavaMethodDeclaration, TsAccessorRole>? accessorRoles,
        int indentLevel)
    {
        if (!cls.ImplementedTypes.Any(t => t.StartsWith("CSharpGenericEnumerator<", StringComparison.Ordinal)))
        {
            return null;
        }
        if (accessorRoles == null)
        {
            return null;
        }
        var hasRealGetCurrent = cls.Methods.Any(m =>
            m.Name == "getCurrent" && m.Parameters.Count == 0 && !accessorRoles.ContainsKey(m));
        if (hasRealGetCurrent)
        {
            return null;
        }
        var currentGetter = cls.Methods.FirstOrDefault(m =>
            m.Name == "getCurrent"
            && m.Parameters.Count == 0
            && accessorRoles.TryGetValue(m, out var role)
            && role is TsAccessorRole.Getter or TsAccessorRole.GetterSetter);
        if (currentGetter == null)
        {
            return null;
        }
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var ret = _exprWriter.MapType(currentGetter.ReturnType);
        return $"{ind}public getCurrent(): {ret} {{ return this.Current; }}";
    }

    /// <summary>
    /// Round 23：CSharpGenericIList&lt;T&gt; implements 契约补发 Java IList 方法面桥接。
    /// C# IList&lt;T&gt; 实现类的索引器发射为 Java 风味 get_Item(index)（Task 16 属性
    /// 重组后仍为方法），而 global.d.ts 的 CSharpGenericIList 接口契约要求 Java IList
    /// 形态 get(index)（消费端 nodes.get(i) 保留该形态）与 toArray()（copyTo/System.
    /// arraycopy 消费）；TS2304 修复后 TS2420 缺口。对含 get_Item、无 get 的实现类
    /// 补发 `get(index: number): T { return this.get_Item(index); }`；无 toArray 的
    /// 补发 `toArray&lt;U = T&gt;(): U[] { return Array.from(this); }`（TS 目标专用）。
    /// </summary>
    public string? EmitIListGetBridge(JavaClassDeclaration cls, int indentLevel)
    {
        if (!cls.ImplementedTypes.Any(t => t.StartsWith("CSharpGenericIList<", StringComparison.Ordinal)))
        {
            return null;
        }
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var blocks = new List<string>();
        var getItem = cls.Methods.FirstOrDefault(m => m.Name == "get_Item" && m.Parameters.Count == 1);
        if (getItem != null && !cls.Methods.Any(m => m.Name == "get" && m.Parameters.Count == 1))
        {
            var ret = _exprWriter.MapType(getItem.ReturnType);
            blocks.Add($"{ind}public get(index: number): {ret} {{ return this.get_Item(index); }}");
        }
        var setItem = cls.Methods.FirstOrDefault(m => m.Name == "set_Item" && m.Parameters.Count == 2);
        if (setItem != null && !cls.Methods.Any(m => m.Name == "set" && m.Parameters.Count == 2))
        {
            var ret = _exprWriter.MapType(setItem.Parameters[1].Type);
            blocks.Add($"{ind}public set(index: number, value: {ret}): void {{ this.set_Item(index, value); }}");
        }
        if (!cls.Methods.Any(m => m.Name == "toArray" && m.Parameters.Count == 0))
        {
            var elementType = cls.ImplementedTypes
                .Where(t => t.StartsWith("CSharpGenericIList<", StringComparison.Ordinal))
                .Select(t => t.Substring("CSharpGenericIList<".Length, t.Length - "CSharpGenericIList<".Length - 1).Trim())
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(elementType))
            {
                blocks.Add($"{ind}public toArray(): {elementType}[] {{ return Array.from(this); }}");
            }
        }
        return blocks.Count > 0 ? string.Join(Environment.NewLine, blocks) : null;
    }

    /// <summary>
    /// 属性 getter 访问器发射（Task 16）：`get X(): T { ... }`。可见性 public 省略
    /// （TS 类成员默认），private/protected/static 保留；方法体复用 EmitMethod 三路发射。
    /// </summary>
    public string EmitPropertyAccessorGetter(JavaMethodDeclaration getter, string propertyName, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        if ((getter.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        else if ((getter.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        if ((getter.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((getter.Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");

        sb.Append($"get {TsNaming.SafeIdentifier(propertyName)}(): {_exprWriter.MapType(getter.ReturnType)}");
        AppendMethodBody(sb, getter, ind, indentLevel);
        return sb.ToString();
    }

    /// <summary>
    /// 属性 setter 访问器发射（Task 16）：`private set X(value: T) { ... }`。
    /// </summary>
    public string EmitPropertyAccessorSetter(JavaMethodDeclaration setter, string propertyName, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        if ((setter.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        else if ((setter.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        if ((setter.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((setter.Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");

        sb.Append($"set {TsNaming.SafeIdentifier(propertyName)}(");
        var parms = setter.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_exprWriter.MapType(p.Type)}");
        sb.Append(string.Join(", ", parms));
        // TS setter 不允许返回类型注解（tsc TS1095），只输出参数列表
        sb.Append(')');
        AppendMethodBody(sb, setter, ind, indentLevel);
        return sb.ToString();
    }

    /// <summary>
    /// 方法体发射（结构化体 > 表达式体 > Body 字符串），EmitMethod 与访问器发射共用。
    /// </summary>
    private void AppendMethodBody(StringBuilder sb, JavaMethodDeclaration method, string ind, int indentLevel)
    {
        if ((method.Modifiers & JavaModifiers.Abstract) != 0
            || (method.Body == null && method.StructuredBody == null))
        {
            sb.Append(';');
            return;
        }
        var shadowNames = MethodShadowNames(method);
        if (method.StructuredBody != null)
        {
            // 结构化方法体优先于 Body 字符串（JavaMethodDeclaration 契约）：
            // 逐语句交给 TsStatementWriter 发射，避免丢失 body
            sb.AppendLine(" {");
            _stmtWriter.MethodLocals = shadowNames;
            _exprWriter.MethodLocals = shadowNames;
            foreach (var stmt in method.StructuredBody.Statements)
            {
                sb.AppendLine(_stmtWriter.Emit(stmt, indentLevel + 2));
            }
            _stmtWriter.MethodLocals = null;
            _exprWriter.MethodLocals = null;
            sb.Append(ind).Append(_indent).Append('}');
            return;
        }
        if (method.IsBodyExpression)
        {
            // 表达式体成员（方法/属性 getter/索引器/运算符）：类成员体不支持 => 语法
            // （TS1144），转块体。对齐 JavaMemberDeclaration 语义：非 void 加 return，
            // void 表达式即语句（无 return）。
            var isVoid = _exprWriter.MapType(method.ReturnType) == "void";
            sb.AppendLine(" {");
            sb.Append(ind).Append(_indent);
            if (!isVoid) sb.Append("return ");
            sb.AppendLine($"{TsRawStatementNormalizer.Normalize(method.Body!, _exprWriter.MapType, _exprWriter.MapExceptionName, memberContext: MemberContext, extraLocals: shadowNames)};");
            sb.Append(ind).Append('}');
            return;
        }
        sb.AppendLine(" {");
        // body 已是字符串，按行缩进
        // body is a raw string: normalize Java text first (declarations/for heads/
        // array literals/float suffixes), then emit per line.
        var normalizedBody = TsRawStatementNormalizer.Normalize(method.Body!, _exprWriter.MapType, _exprWriter.MapExceptionName, memberContext: MemberContext, extraLocals: shadowNames);
        foreach (var line in normalizedBody.Split('\n'))
        {
            sb.Append(ind).Append(_indent).AppendLine(line);
        }
        sb.Append(ind).Append('}');
    }

    /// <summary>
    /// 测试类字段发射（Task 39）：vitest describe 作用域 let 变量（剥修饰符）。
    /// 无初始化器补 null 零值（与 EmitField 的 strictNullChecks 宽化一致）。
    /// </summary>
    public string EmitTestField(JavaFieldDeclaration field, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);
        var tsType = _exprWriter.MapType(field.Type);
        var name = TsNaming.SafeIdentifier(field.Name);
        var hasInitializer = !string.IsNullOrEmpty(field.Initializer);
        var emittedType = tsType;
        if (!hasInitializer
            && !tsType.Contains("| null") && !tsType.Contains("null |")
            && tsType is not ("null" or "any" or "undefined" or "void"))
        {
            emittedType = tsType.Contains("=>") ? $"({tsType}) | null" : $"{tsType} | null";
        }
        sb.Append($"let {name}: {emittedType}");
        if (hasInitializer)
        {
            sb.Append($" = {NormalizeLiteral(field.Initializer!, MemberContext)}");
        }
        else
        {
            sb.Append(" = null");
        }
        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// 测试方法发射（Task 39）：[Fact] → it("名", () => { ... })；
    /// [Theory]+InlineData → it.each([...])("名", (p1, p2) => { ... })。
    /// async 方法回调加 async 前缀；方法体复用三路发射（结构化体/表达式体/Body 字符串）。
    /// </summary>
    public string EmitTestMethod(JavaMethodDeclaration method, TsTestMethodInfo info, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);
        var asyncPrefix = method.IsAsync ? "async " : "";
        if (info.IsTheory && info.HasInlineData)
        {
            sb.AppendLine("it.each([");
            foreach (var row in info.InlineData)
            {
                sb.Append(ind).Append(_indent).AppendLine($"[{string.Join(", ", row)}],");
            }
            sb.Append(ind).Append("])");
            sb.Append($"(\"{TsNaming.EscapeStringLiteral(info.MethodName)}\", {asyncPrefix}(");
            sb.Append(string.Join(", ", method.Parameters.Select(p => TsNaming.SafeIdentifier(p.Name))));
            sb.Append(") =>");
        }
        else
        {
            sb.Append($"it(\"{TsNaming.EscapeStringLiteral(info.MethodName)}\", {asyncPrefix}() =>");
        }
        AppendArrowBody(sb, method, ind, indentLevel);
        sb.Append(");");
        return sb.ToString();
    }

    /// <summary>
    /// 测试类非测试方法发射（Task 39）：describe 作用域局部函数（剥访问修饰符；
    /// 测试类辅助方法保持可调用）。
    /// </summary>
    public string EmitLocalFunction(JavaMethodDeclaration method, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);
        if (method.IsAsync)
        {
            sb.Append("async ");
        }
        var name = TsNaming.SafeIdentifier(method.Name);
        sb.Append($"function {name}{TsTypeWriter.FormatTypeParameters(method.TypeParameters, MapType)}(");
        var parms = method.Parameters.Select(p =>
        {
            var pName = TsNaming.SafeIdentifier(p.Name);
            return $"{pName}: {_exprWriter.MapType(p.Type)}";
        });
        sb.Append(string.Join(", ", parms));
        sb.Append($"): {_exprWriter.MapType(method.ReturnType)}");
        AppendMethodBody(sb, method, ind, indentLevel);
        return sb.ToString();
    }

    /// <summary>
    /// 箭头函数体发射（Task 39）：it/it.each 回调体；与 AppendMethodBody 三路对应，
    /// 收尾不补分号（调用方追加）。
    /// </summary>
    private void AppendArrowBody(StringBuilder sb, JavaMethodDeclaration method, string ind, int indentLevel)
    {
        var shadowNames = MethodShadowNames(method);
        if (method.StructuredBody != null)
        {
            sb.AppendLine(" {");
            _stmtWriter.MethodLocals = shadowNames;
            _exprWriter.MethodLocals = shadowNames;
            foreach (var stmt in method.StructuredBody.Statements)
            {
                sb.AppendLine(_stmtWriter.Emit(stmt, indentLevel + 2));
            }
            _stmtWriter.MethodLocals = null;
            _exprWriter.MethodLocals = null;
            sb.Append(ind).Append('}');
            return;
        }
        if (method.IsBodyExpression)
        {
            sb.Append(" => ").Append(TsRawStatementNormalizer.Normalize(method.Body!, _exprWriter.MapType, _exprWriter.MapExceptionName, memberContext: MemberContext, extraLocals: shadowNames));
            return;
        }
        if (method.Body == null)
        {
            sb.Append(" => { }");
            return;
        }
        sb.AppendLine(" {");
        var normalizedArrowBody = TsRawStatementNormalizer.Normalize(method.Body!, _exprWriter.MapType, _exprWriter.MapExceptionName, memberContext: MemberContext, extraLocals: shadowNames);
        foreach (var line in normalizedArrowBody.Split('\n'))
        {
            sb.Append(ind).Append(_indent).AppendLine(line);
        }
        sb.Append(ind).Append('}');
    }

    public string EmitConstructor(JavaConstructorDeclaration ctor, string? extendedType, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // TS 支持 private/protected constructor（I4）；public 为默认可省略
        if ((ctor.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        else if ((ctor.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");

        sb.Append("constructor(");
        var parms = ctor.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_exprWriter.MapType(p.Type)}");
        sb.Append(string.Join(", ", parms));
        sb.Append(')');

        sb.AppendLine(" {");

        // 基类构造调用：IR 中为 Java 风味字符串（实际属性名 Initializer，非 BaseConstructorCall），
        // 如 "super(a, b)"。TS 要求派生类构造器必须显式调用 super()（TS2377/TS17009）：
        // 类有 ExtendedType 时无条件发射 super(...)（有实参原样，无实参 super();）。
        // "this(...)" 重载委托无 TS 对应 → 诊断注释（重载构造器在 M3 转工厂）。
        if (!string.IsNullOrEmpty(ctor.Initializer))
        {
            if (ctor.Initializer.StartsWith("super("))
            {
                sb.Append(ind).Append(_indent).AppendLine($"{ctor.Initializer};");
            }
            else
            {
                // this(...) 委托：先满足基类 super() 要求，再注释说明委托无法直译
                if (!string.IsNullOrEmpty(extendedType))
                {
                    sb.Append(ind).Append(_indent).AppendLine("super();");
                }
                sb.Append(ind).Append(_indent).AppendLine($"/* cs2j: unsupported ctor delegation: {ctor.Initializer} */");
            }
        }
        else if (!string.IsNullOrEmpty(extendedType))
        {
            sb.Append(ind).Append(_indent).AppendLine("super();");
        }

        var ctorLocals = new System.Collections.Generic.HashSet<string>(
            ctor.Parameters.Select(p => TsNaming.SafeIdentifier(p.Name)), System.StringComparer.Ordinal);
        if (ctor.StructuredBody != null)
        {
            _stmtWriter.MethodLocals = ctorLocals;
            _exprWriter.MethodLocals = ctorLocals;
            foreach (var stmt in ctor.StructuredBody.Statements)
            {
                sb.AppendLine(_stmtWriter.Emit(stmt, indentLevel + 1));
            }
            _stmtWriter.MethodLocals = null;
            _exprWriter.MethodLocals = null;
        }
        else if (!string.IsNullOrEmpty(ctor.Body))
        {
            // Round 16：ctor raw 体此前完全不归一化；接入完整 Normalize 管线
            // （成员前缀 + 既有的声明 let 化/异常名映射等），成员上下文与参数遮蔽随行。
            var normalizedBody = TsRawStatementNormalizer.Normalize(ctor.Body!, _exprWriter.MapType, _exprWriter.MapExceptionName, memberContext: MemberContext, extraLocals: ctorLocals);
            var bodyLines = normalizedBody.Split('\n');
            foreach (var line in bodyLines)
            {
                sb.Append(ind).Append(_indent).AppendLine(line);
            }
        }
        sb.Append(ind).Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 返回 TS 类型的默认零值（spec §2.5 字段默认零值规则）。
    /// </summary>
    public static string DefaultValueForType(string tsType)
    {
        if (string.IsNullOrEmpty(tsType)) return "null";

        // 可空类型 X | null -> null
        if (tsType.Contains("| null") || tsType.Contains("null |")) return "null";

        // 数组 -> []
        if (tsType.EndsWith("[]")) return "[]";

        return tsType switch
        {
            "number" => "0",
            "bigint" => "0n",
            "boolean" => "false",
            "string" => "null",  // C# string 默认 null，不用 ""
            "void" => "undefined",
            "any" => "null",
            "null" => "null",
            "undefined" => "undefined",
            _ => "null",  // 引用类型默认 null
        };
    }

    /// <summary>
    /// 方法发射遮蔽名 = 参数 + 方法类型参数 + 结构化局部变量（参数/局部遮蔽成员，禁止加前缀）。
    /// </summary>
    private static HashSet<string> MethodShadowNames(JavaMethodDeclaration method)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var parameter in method.Parameters)
        {
            names.Add(TsNaming.SafeIdentifier(parameter.Name));
        }
        foreach (var typeParameter in method.TypeParameters)
        {
            names.Add(typeParameter.Name);
        }
        if (method.StructuredBody != null)
        {
            CollectStructuredLocalNames(method.StructuredBody.Statements, names);
        }
        return names;
    }

    /// <summary>收集结构化语句树的局部变量名（与 TsPropertyReconstructionRewriter 同款，仅名字）。</summary>
    private static void CollectStructuredLocalNames(IReadOnlyList<JavaStatement> statements, HashSet<string> names)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case JavaVariableDeclarationStatement declaration:
                    names.Add(declaration.Name);
                    break;
                case JavaRawStatement raw:
                    // Round 16 跨语句遮蔽：raw 语句声明的局部名（var settings = ...; /
                    // Type name = ...; / 经典 for 头）收集进方法级遮蔽集，供后续语句的
                    // raw 表达式归一化（MSAGL GTreeOverlapRemoval：Create$1(settings, ...)
                    // 不得补 this.——静态方法内 this.settings 是 TS2339）。
                    TsRawStatementNormalizer.CollectDeclaredLocalNames(raw.Code, names);
                    break;
                case JavaForEachStatement forEach:
                    if (!string.IsNullOrEmpty(forEach.VariableName)) names.Add(forEach.VariableName);
                    CollectStructuredLocalNamesFromStatement(forEach.Body, names);
                    break;
                case JavaBlockStatement block:
                    CollectStructuredLocalNames(block.Statements, names);
                    break;
                case JavaIfStatement ifStatement:
                    CollectStructuredLocalNamesFromStatement(ifStatement.ThenBody, names);
                    if (ifStatement.ElseBody != null) CollectStructuredLocalNamesFromStatement(ifStatement.ElseBody, names);
                    break;
                case JavaWhileStatement whileStatement:
                    CollectStructuredLocalNamesFromStatement(whileStatement.Body, names);
                    break;
                case JavaDoWhileStatement doWhile:
                    CollectStructuredLocalNamesFromStatement(doWhile.Body, names);
                    break;
                case JavaForStatement forStatement:
                    CollectForInitNames(forStatement.Initializer, names);
                    CollectStructuredLocalNamesFromStatement(forStatement.Body, names);
                    break;
                case JavaTryCatchStatement tryCatch:
                    CollectStructuredLocalNames(tryCatch.TryBody.Statements, names);
                    foreach (var catchClause in tryCatch.CatchClauses)
                    {
                        if (!string.IsNullOrEmpty(catchClause.VariableName)) names.Add(catchClause.VariableName);
                        CollectStructuredLocalNames(catchClause.Body.Statements, names);
                    }
                    if (tryCatch.FinallyBody != null) CollectStructuredLocalNames(tryCatch.FinallyBody.Statements, names);
                    break;
                case JavaSwitchStatement switchStatement:
                    foreach (var section in switchStatement.Sections)
                    {
                        CollectStructuredLocalNames(section.Statements, names);
                    }
                    break;
            }
        }
    }

    private static void CollectStructuredLocalNamesFromStatement(JavaStatement statement, HashSet<string> names)
    {
        switch (statement)
        {
            case JavaBlockStatement block:
                CollectStructuredLocalNames(block.Statements, names);
                break;
            case JavaVariableDeclarationStatement declaration:
                names.Add(declaration.Name);
                break;
            case JavaForEachStatement forEach:
                if (!string.IsNullOrEmpty(forEach.VariableName)) names.Add(forEach.VariableName);
                CollectStructuredLocalNamesFromStatement(forEach.Body, names);
                break;
            case JavaIfStatement ifStatement:
                CollectStructuredLocalNamesFromStatement(ifStatement.ThenBody, names);
                if (ifStatement.ElseBody != null) CollectStructuredLocalNamesFromStatement(ifStatement.ElseBody, names);
                break;
            case JavaWhileStatement whileStatement:
                CollectStructuredLocalNamesFromStatement(whileStatement.Body, names);
                break;
            case JavaDoWhileStatement doWhile:
                CollectStructuredLocalNamesFromStatement(doWhile.Body, names);
                break;
            case JavaForStatement forStatement:
                CollectForInitNames(forStatement.Initializer, names);
                CollectStructuredLocalNamesFromStatement(forStatement.Body, names);
                break;
            case JavaTryCatchStatement tryCatch:
                CollectStructuredLocalNames(tryCatch.TryBody.Statements, names);
                foreach (var catchClause in tryCatch.CatchClauses)
                {
                    if (!string.IsNullOrEmpty(catchClause.VariableName)) names.Add(catchClause.VariableName);
                    CollectStructuredLocalNames(catchClause.Body.Statements, names);
                }
                if (tryCatch.FinallyBody != null) CollectStructuredLocalNames(tryCatch.FinallyBody.Statements, names);
                break;
            case JavaSwitchStatement switchStatement:
                foreach (var section in switchStatement.Sections)
                {
                    CollectStructuredLocalNames(section.Statements, names);
                }
                break;
        }
    }

    /// <summary>经典 for 头声明段变量名（"int i = 0, j = 1" → i, j；按顶层逗号分段）。</summary>
    private static void CollectForInitNames(string? initializer, HashSet<string> names)
    {
        if (string.IsNullOrEmpty(initializer))
        {
            return;
        }
        foreach (var segment in initializer.Split(','))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            var eq = trimmed.IndexOf('=');
            var head = eq < 0 ? trimmed : trimmed[..eq];
            var tokens = head.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0)
            {
                names.Add(tokens[^1]);
            }
        }
    }

    /// <summary>
    /// 将 Java 风味字面量归一化为合法 TS 字面量（I2，等价于 TsExpressionWriter.EmitLiteral 的
    /// 字符串版）：long/ulong 后缀 L → bigint 后缀 n（100L→100n、0xFFL→0xFFn、1_000L→1_000n）；
    /// float/double 后缀 f/d → 去掉（1.5f→1.5）；char 字面量 'a' → "a"。
    /// 仅当整个字符串为字面量形态时才转换，避免误伤表达式/标识符。
    /// Round 19：尾路径归一化传入 _exprWriter.MapType（字段初始化器 raw 文本中的 FQCN——
    /// "private l: Point = new Microsoft.Msagl.Core.Geometry.Point();"（CubicBezierSegment
    /// 等 146 处 TS2304 'Microsoft'）此前 mapType 为 null 直通）。
    /// </summary>
    private string NormalizeLiteral(string literal, TsMemberContext? memberContext)
    {
        // Task 19 注记⑦：Java 风味数值 cast 前缀（(long)/(int)/(short)/(byte)/(double)/(float)）
        // const long = uint.MaxValue 等初始化经共享适配层包成 "(long) (4294967295L)"
        // 形态，TS 无 cast 语法，需剥除前缀并把 L 后缀归一化为 n
        // （递归处理嵌套/括号包裹形态；仅字面量整体匹配才转换）
        var stripped = StripNumericCastPrefix(literal);
        if (stripped != literal)
        {
            return NormalizeLiteral(stripped, memberContext);
        }

        if (literal.Length >= 3 && literal[0] == '\'' && literal[^1] == '\'' && !literal[1..^1].Contains('"'))
        {
            return "\"" + literal[1..^1] + "\"";
        }

        if (literal.EndsWith("L", StringComparison.Ordinal) && IsNumericLiteralBody(literal[..^1]))
        {
            return literal[..^1] + "n";
        }

        if (literal.EndsWith("f", StringComparison.OrdinalIgnoreCase) && IsNumericLiteralBody(literal[..^1]))
        {
            return literal[..^1];
        }

        // Task 19 note (7): under the TS target, const initializers such as uint.MaxValue
        // reach this writer as BigInt(<Java literal>)/Number(<Java literal>) because the
        // shared adapt layer wraps them (AdaptExpressionToConvertedType TS branch). The
        // inner literal still carries Java suffixes, so normalize it: BigInt(4294967295L)
        // -> 4294967295n, Number(1.5f) -> 1.5, BigInt(5) -> 5n.
        if (literal.StartsWith("BigInt(", StringComparison.Ordinal) && literal.EndsWith(")", StringComparison.Ordinal))
        {
            var inner = literal["BigInt(".Length..^1].Trim();
            if (inner.EndsWith("L", StringComparison.Ordinal) && IsNumericLiteralBody(inner[..^1]))
            {
                return inner[..^1] + "n";
            }
            if (IsNumericLiteralBody(inner))
            {
                return inner + "n";
            }
            return literal;
        }

        if (literal.StartsWith("Number(", StringComparison.Ordinal) && literal.EndsWith(")", StringComparison.Ordinal))
        {
            var inner = literal["Number(".Length..^1].Trim();
            if ((inner.EndsWith("L", StringComparison.Ordinal) && IsNumericLiteralBody(inner[..^1]))
                || (inner.EndsWith("f", StringComparison.OrdinalIgnoreCase) && IsNumericLiteralBody(inner[..^1])))
            {
                return inner[..^1];
            }
            if (IsNumericLiteralBody(inner))
            {
                return inner;
            }
            return literal;
        }

        // M5 / MSAGL: Java array creation literals (new String[] { "a" } /
        // new double[][] { {...} }) reach field initializers as raw text; route them
        // through the raw normalizer (array literal + float suffix passes).
        return TsRawStatementNormalizer.Normalize(literal, _exprWriter.MapType, memberContext: memberContext);
    }

    /// <summary>剥除 "({type}) (literal)" 或 "({type})literal" 形态的 Java 数值 cast 前缀，
    /// 供 NormalizeLiteral 归一化 const 字段初始化器（TS 无 cast 语法）。</summary>
    private static string StripNumericCastPrefix(string literal)
    {
        var trimmed = literal.TrimStart();
        if (!trimmed.StartsWith('('))
        {
            return literal;
        }
        var closeIdx = trimmed.IndexOf(')');
        if (closeIdx < 2)
        {
            return literal;
        }
        var typeName = trimmed[1..closeIdx];
        if (typeName is not ("long" or "int" or "short" or "byte" or "sbyte" or "double" or "float"))
        {
            return literal;
        }
        var rest = trimmed[(closeIdx + 1)..].Trim();
        if (rest.Length >= 2 && rest[0] == '(' && rest[^1] == ')')
        {
            rest = rest[1..^1];
        }
        if (rest.Length == 0)
        {
            return literal;
        }
        if (rest.EndsWith("L", StringComparison.Ordinal) && IsNumericLiteralBody(rest[..^1]))
        {
            return rest;
        }
        if (IsNumericLiteralBody(rest))
        {
            return rest;
        }
        return literal;
    }

    private static bool IsNumericLiteralBody(string body)
    {
        if (body.Length == 0) return false;

        // 十六进制（C# uint/ulong 常量渲染为 0x... / 0x...L），可含下划线
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return body.Length > 2 && body[2..].All(c => Uri.IsHexDigit(c) || c == '_');
        }

        // 十进制整数/浮点，可含下划线、小数点、指数（100、1_000、1.5、1e5、-100）
        var start = body[0] is '+' or '-' ? 1 : 0;
        var anyDigit = false;
        var anyFraction = false;
        var anyExponent = false;
        for (var i = start; i < body.Length; i++)
        {
            var c = body[i];
            if (char.IsAsciiDigit(c)) { anyDigit = true; continue; }
            if (c == '_') continue;
            if (c == '.' && !anyFraction && !anyExponent) { anyFraction = true; continue; }
            if ((c == 'e' || c == 'E') && anyDigit && !anyExponent) { anyExponent = true; continue; }
            if ((c == '+' || c == '-') && anyExponent && i > start) { continue; }
            return false;
        }
        return anyDigit;
    }
}
