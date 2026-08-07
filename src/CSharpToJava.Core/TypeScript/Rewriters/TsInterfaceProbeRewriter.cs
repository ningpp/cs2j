using System.Text;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 接口运行时探针 rewriter（Task 25，M3，spec §2.6.3）。
/// ① 每接口生成 is$IXxx 探针函数：必需方法存在性检查（typeof (v as IX).m === "function"）
///    + 继承链（ExtendedTypes 递归）方法合并；纯属性/无方法接口 → 仅 CsRuntime.isMarked
///    快路径 + "structural probe" 诊断；
/// ② is/as 调用点改写（raw 文本扫描 + 结构化 JavaInstanceOfExpression）：
///    {expr} instanceof {Iface} → is$Iface({expr})（声明模式变量形态跳过，登记已知限制）；
///    (Iface)({expr}) / (Iface&lt;Args&gt;)({expr}) 接口强转 → ({expr} as {Iface[&lt;映射实参&gt;]})；
///    as 三元式整体改写（探针守卫 + as 强转 + null 分支，strict 下 null 分支类型不符，
///    整体再 as 回目标类型）：`(x instanceof IFoo ? (IFoo)(x) : null)` →
///    `(is$IFoo(x) ? (x as IFoo) : null) as IFoo`；
/// ③ 实现类构造器注入 CsRuntime.markImplements(this, "IFoo")（无构造器类合成 constructor()）。
/// 探针文本登记进 context.TsInterfaceProbes，由 TypeScriptEmitter 在类型声明后追加；
/// CsRuntime 登记进 context.TsCompatSymbols 生成 @csharptypescript/compat 导入。
/// 已知限制（登记于计划 Task 25）：switch 模式变量 `case IFoo f:`（前端产出
/// `(x instanceof IFoo f)` Java 16 形态）不改写（Task 27 模式匹配处理）。
/// </summary>
public sealed class TsInterfaceProbeRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext _context;
    private readonly HashSet<string> _diagnosed = new(StringComparer.Ordinal);

    /// <summary>裸接口名 → 接口声明（嵌套接口与 TS 发射一致按扁平名登记）。</summary>
    private readonly Dictionary<string, JavaInterfaceDeclaration> _interfaces = new(StringComparer.Ordinal);

    /// <summary>泛型实参 Java 名 → TS 名（强转类型参数归一化，如 IRepo&lt;String&gt; → IRepo&lt;string&gt;）。</summary>
    private static readonly Dictionary<string, string> s_tsTypeArgMap = new(StringComparer.Ordinal)
    {
        ["String"] = "string",
        ["Integer"] = "number",
        ["Long"] = "bigint",
        ["Short"] = "number",
        ["Byte"] = "number",
        ["Double"] = "number",
        ["Float"] = "number",
        ["Boolean"] = "boolean",
        ["Character"] = "string",
        ["Object"] = "any",
        ["int"] = "number",
        ["long"] = "bigint",
        ["boolean"] = "boolean",
        ["char"] = "string",
        ["double"] = "number",
        ["float"] = "number",
        ["short"] = "number",
        ["byte"] = "number",
    };

    public TsInterfaceProbeRewriter(ConversionContext context)
    {
        _context = context;
    }

    // ─── 探针收集与生成 ────────────────────────────────────────────────

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        foreach (var type in node.TypeDeclarations)
        {
            CollectInterfaces(type);
        }
        if (_interfaces.Count > 0)
        {
            _context.TsCompatSymbols.Add("CsRuntime");
            foreach (var iface in _interfaces.Values)
            {
                GenerateProbe(iface);
            }
        }
        return base.VisitCompilationUnit(node);
    }

    private void CollectInterfaces(JavaTypeDeclaration type)
    {
        switch (type)
        {
            case JavaInterfaceDeclaration iface:
                _interfaces.TryAdd(iface.Name, iface);
                foreach (var nested in iface.NestedTypes)
                {
                    CollectInterfaces(nested);
                }
                break;
            case JavaClassDeclaration cls:
                foreach (var nested in cls.NestedTypes)
                {
                    CollectInterfaces(nested);
                }
                break;
        }
    }

    private void GenerateProbe(JavaInterfaceDeclaration iface)
    {
        var name = iface.Name;
        var methods = CollectMethodNames(iface);
        var probeName = $"is${name}";
        string probe;
        if (methods.Count == 0)
        {
            Warn($"cs2j: interface {name} has no probed methods — probe is mark-only (structural probe skipped) — spec §2.6.3");
            probe = $"export function {probeName}(v: unknown): boolean {{\n" +
                    $"    return CsRuntime.isMarked(v, \"{name}\");\n" +
                    "}";
        }
        else
        {
            var castType = iface.TypeParameters.Count > 0 ? $"{name}<any>" : name;
            var checks = string.Join(
                "\n            && ",
                methods.Select(m => $"typeof (v as {castType}).{m} === \"function\""));
            probe = $"export function {probeName}(v: unknown): boolean {{\n" +
                    $"    return CsRuntime.isMarked(v, \"{name}\")\n" +
                    "        || (typeof v === \"object\" && v !== null\n" +
                    $"            && {checks});\n" +
                    "}";
        }
        _context.TsInterfaceProbes ??= new Dictionary<string, string>(StringComparer.Ordinal);
        _context.TsInterfaceProbes[name] = probe;
    }

    private List<string> CollectMethodNames(JavaInterfaceDeclaration iface)
    {
        var names = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var visitedIfaces = new HashSet<JavaInterfaceDeclaration>();
        CollectMethodNamesRecursive(iface, names, seenNames, visitedIfaces);
        return names;
    }

    private void CollectMethodNamesRecursive(
        JavaInterfaceDeclaration iface,
        List<string> names,
        HashSet<string> seenNames,
        HashSet<JavaInterfaceDeclaration> visitedIfaces)
    {
        if (!visitedIfaces.Add(iface))
        {
            return;
        }
        foreach (var method in iface.Methods)
        {
            if (IsPropertyAccessor(method))
            {
                continue;
            }
            var tsName = TsNaming.SafeIdentifier(method.Name);
            if (seenNames.Add(tsName))
            {
                names.Add(tsName);
            }
        }
        foreach (var extended in iface.ExtendedTypes)
        {
            var bare = StripTypeArgs(extended);
            var simple = SimpleName(bare);
            if (_interfaces.TryGetValue(simple, out var baseIface))
            {
                CollectMethodNamesRecursive(baseIface, names, seenNames, visitedIfaces);
            }
        }
    }

    /// <summary>与 TsTypeWriter.EmitInterface 一致的属性访问器判定（IsAutoGenerated + get/set 前缀）。</summary>
    private static bool IsPropertyAccessor(JavaMethodDeclaration method)
    {
        if (!method.IsAutoGenerated || method.Name.Length <= 3)
        {
            return false;
        }
        var isGet = method.Name.StartsWith("get");
        var isSet = method.Name.StartsWith("set");
        if (!isGet && !isSet)
        {
            return false;
        }
        if (isGet && method.Parameters.Count > 0)
        {
            return false;
        }
        var propName = method.Name.Substring(3);
        return propName.Length > 0 && char.IsUpper(propName[0]);
    }

    // ─── 实现类构造器注入 markImplements ───────────────────────────────

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var visited = base.VisitClassDeclaration(node);
        foreach (var block in node.StaticInitializers)
        {
            for (var i = 0; i < block.Statements.Count; i++)
            {
                block.Statements[i] = RewriteBodyText(block.Statements[i]);
            }
        }
        var implemented = node.ImplementedTypes
            .Select(t => SimpleName(StripTypeArgs(t)))
            .Where(_interfaces.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (implemented.Count == 0)
        {
            return visited;
        }
        var mark = BuildMarkStatement(implemented);
        if (node.Constructors.Count > 0)
        {
            foreach (var ctor in node.Constructors)
            {
                InjectMark(ctor, mark);
            }
        }
        else
        {
            // 无显式构造器：合成 constructor() 承接标记（TsTypeWriter 现有逻辑发射首构造器）
            node.Constructors.Add(new JavaConstructorDeclaration
            {
                ClassName = node.Name,
                Body = mark,
            });
        }
        return visited;
    }

    private static string BuildMarkStatement(List<string> interfaceNames)
    {
        var names = string.Join(", ", interfaceNames.Select(n => $"\"{n}\""));
        return $"CsRuntime.markImplements(this, {names});";
    }

    private static void InjectMark(JavaConstructorDeclaration ctor, string mark)
    {
        if (ctor.StructuredBody != null)
        {
            ctor.StructuredBody.Statements.Insert(0, new JavaRawStatement(mark));
        }
        else
        {
            var body = ctor.Body ?? string.Empty;
            ctor.Body = body.Length == 0 ? mark : mark + "\n" + body;
        }
    }

    // ─── raw 文本改写引擎 ──────────────────────────────────────────────

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        var visited = base.VisitMethodDeclaration(node);
        if (!string.IsNullOrEmpty(visited.Body))
        {
            visited.Body = RewriteBodyText(visited.Body);
        }
        return visited;
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        var visited = base.VisitConstructorDeclaration(node);
        if (!string.IsNullOrEmpty(visited.Body))
        {
            visited.Body = RewriteBodyText(visited.Body);
        }
        return visited;
    }

    public override JavaFieldDeclaration VisitFieldDeclaration(JavaFieldDeclaration node)
    {
        if (!string.IsNullOrEmpty(node.Initializer))
        {
            node.Initializer = RewriteBodyText(node.Initializer);
        }
        return node;
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        node.Code = RewriteBodyText(node.Code);
        return node;
    }

    public override JavaRawExpression VisitRawExpression(JavaRawExpression node)
    {
        node.Code = RewriteBodyText(node.Code);
        return node;
    }

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        // 结构化 instanceof：接口类型 → 探针调用（返回类型不定，统一在此分发；
        // 声明模式变量跳过，登记已知限制）
        return node switch
        {
            JavaInstanceOfExpression instanceOf => VisitInstanceOf(instanceOf),
            _ => base.VisitExpression(node),
        };
    }

    private JavaExpression VisitInstanceOf(JavaInstanceOfExpression node)
    {
        node.Expression = VisitExpression(node.Expression);
        if (node.PatternVariable == null)
        {
            var simple = SimpleName(StripTypeArgs(node.Type));
            if (_interfaces.ContainsKey(simple))
            {
                return new JavaMethodCallExpression
                {
                    MethodName = $"is${simple}",
                    Arguments = { node.Expression },
                };
            }
        }
        return node;
    }

    private string RewriteBodyText(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = TsGenericErasureIrRewriter.SkipQuotedLiteral(text, i);
                sb.Append(text, start, i - start);
                continue;
            }
            if (TryMatchInterfaceCast(text, i, out var castInner, out var castType, out var consumedCast))
            {
                sb.Append($"({castInner} as {castType})");
                i += consumedCast;
                continue;
            }
            if (TryMatchInterfaceInstanceOf(text, i, out var instanceExpr, out var probeName,
                    out var ternaryInner, out var ternaryType, out var spanStart, out var consumedInstanceOf))
            {
                // 匹配从 instanceof 位置发起，替换跨度 text[spanStart..) 的前段已被复制进
                // 输出，需先回退再追加改写结果
                sb.Length -= i - spanStart;
                if (ternaryInner != null)
                {
                    // as 三元式整体改写：探针守卫 + as 强转 + null 分支；
                    // strict 下 null 分支类型不符，整体再 as 回目标类型（C# as 语义
                    // 运行时仍可返回 null，非空性为编译期声明）
                    sb.Append($"({probeName}({instanceExpr}) ? ({ternaryInner} as {ternaryType}) : null) as {ternaryType}");
                }
                else
                {
                    sb.Append($"{probeName}({instanceExpr})");
                }
                i = spanStart + consumedInstanceOf;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>匹配 {expr} instanceof {Iface}（裸名或限定名；非声明模式变量）。
    /// as 三元式（? ({Cast})({expr2}) : null，外层括号可选）整体改写并返回 castInner/castType；
    /// 其余形态仅改写 instanceof 部分。spanStart/consumed 描述替换跨度。</summary>
    private bool TryMatchInterfaceInstanceOf(string text, int i, out string instanceExpr, out string probeName,
        out string? castInner, out string? castType, out int spanStart, out int consumed)
    {
        instanceExpr = string.Empty;
        probeName = string.Empty;
        castInner = null;
        castType = null;
        spanStart = -1;
        consumed = 0;
        if (i + "instanceof".Length > text.Length
            || !text.AsSpan(i).StartsWith("instanceof", StringComparison.Ordinal))
        {
            return false;
        }
        if (i > 0 && TsGenericErasureIrRewriter.IsIdentifierChar(text[i - 1]))
        {
            return false;
        }
        var nameStart = i + "instanceof".Length;
        if (nameStart >= text.Length || text[nameStart] != ' ')
        {
            return false;
        }
        nameStart++;
        var qualified = ReadQualifiedName(text, nameStart);
        if (qualified.Length == 0)
        {
            return false;
        }
        var afterName = nameStart + qualified.Length;
        // 声明模式变量（x instanceof IFoo f）：Task 27 模式匹配处理，此处不改写
        if (afterName < text.Length && TsGenericErasureIrRewriter.IsIdentifierChar(text[afterName]))
        {
            return false;
        }
        var simple = SimpleName(qualified);
        if (!_interfaces.ContainsKey(simple))
        {
            return false;
        }
        // 反向提取 instanceof 左侧表达式
        var exprEnd = i;
        var exprStart = exprEnd - 1;
        while (exprStart >= 0 && char.IsWhiteSpace(text[exprStart]))
        {
            exprStart--;
        }
        exprStart = TsGenericErasureIrRewriter.BacktrackExpressionStart(text, exprStart);
        if (exprStart < 0 || exprStart >= exprEnd)
        {
            return false;
        }
        instanceExpr = text[exprStart..exprEnd].Trim();
        probeName = $"is${simple}";

        // ── as 三元式：? ({Cast})({expr2}) : null ──
        var pos = afterName;
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
        {
            pos++;
        }
        if (pos < text.Length && text[pos] == '?')
        {
            pos++;
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }
            if (pos < text.Length && text[pos] == '('
                && TryMatchCastType(text, pos, out var castName, out var castArgs, out var castTypePos)
                && castTypePos + 1 < text.Length && text[castTypePos + 1] == '(')
            {
                var innerOpen = castTypePos + 1;
                var innerClose = TsGenericErasureIrRewriter.FindMatchingCloseParen(text, innerOpen);
                if (innerClose >= 0)
                {
                    var afterInner = innerClose + 1;
                    while (afterInner < text.Length && char.IsWhiteSpace(text[afterInner]))
                    {
                        afterInner++;
                    }
                    if (afterInner < text.Length && text[afterInner] == ':')
                    {
                        afterInner++;
                        while (afterInner < text.Length && char.IsWhiteSpace(text[afterInner]))
                        {
                            afterInner++;
                        }
                        if (afterInner + 4 <= text.Length
                            && text.AsSpan(afterInner).StartsWith("null", StringComparison.Ordinal)
                            && (afterInner + 4 >= text.Length
                                || !TsGenericErasureIrRewriter.IsIdentifierChar(text[afterInner + 4])))
                        {
                            var castSimple = SimpleName(castName);
                            if (_interfaces.ContainsKey(castSimple))
                            {
                                var nullEnd = afterInner + 4;
                                spanStart = exprStart;
                                var end = nullEnd;
                                // 外层括号恰包裹整个三元式（如 (x instanceof IFoo ? (IFoo)(x) : null)）
                                if (exprStart > 0 && text[exprStart - 1] == '(')
                                {
                                    var outerClose = TsGenericErasureIrRewriter.FindMatchingCloseParen(text, exprStart - 1);
                                    if (outerClose == nullEnd)
                                    {
                                        spanStart = exprStart - 1;
                                        end = nullEnd + 1;
                                    }
                                }
                                castInner = text[(innerOpen + 1)..innerClose].Trim();
                                castType = castArgs.Length > 0
                                    ? $"{castSimple}<{MapTypeArgs(castArgs)}>"
                                    : castSimple;
                                consumed = end - spanStart;
                                return true;
                            }
                        }
                    }
                }
            }
            // 三元式形态不完整（如假分支非 null）：回退为普通 is 改写
        }
        spanStart = exprStart;
        consumed = afterName - exprStart;
        return true;
    }

    /// <summary>匹配 (Type) / (Type&lt;Args&gt;) 类型组（括号内限定位），返回类型名、实参与 ')' 位置。</summary>
    private static bool TryMatchCastType(string text, int open, out string name, out string typeArgs, out int closePos)
    {
        name = string.Empty;
        typeArgs = string.Empty;
        closePos = -1;
        if (text[open] != '(')
        {
            return false;
        }
        var nameStart = open + 1;
        var qualified = ReadQualifiedName(text, nameStart);
        if (qualified.Length == 0)
        {
            return false;
        }
        var pos = nameStart + qualified.Length;
        if (pos < text.Length && text[pos] == '<')
        {
            var gt = FindMatchingAngle(text, pos);
            if (gt < 0)
            {
                return false;
            }
            typeArgs = text[(pos + 1)..gt];
            pos = gt + 1;
        }
        if (pos >= text.Length || text[pos] != ')')
        {
            return false;
        }
        name = qualified;
        closePos = pos;
        return true;
    }

    /// <summary>匹配 ({Iface})({expr}) / ({Iface}&lt;Args&gt;)({expr}) 接口强转，返回表达式与 TS 类型。</summary>
    private bool TryMatchInterfaceCast(string text, int i, out string castInner, out string castType, out int consumed)
    {
        castInner = string.Empty;
        castType = string.Empty;
        consumed = 0;
        if (text[i] != '(')
        {
            return false;
        }
        // 前边界非标识符（避免 f(IFoo)(x) 这类调用形态误判）
        if (i > 0 && TsGenericErasureIrRewriter.IsIdentifierChar(text[i - 1]))
        {
            return false;
        }
        var nameStart = i + 1;
        var qualified = ReadQualifiedName(text, nameStart);
        if (qualified.Length == 0)
        {
            return false;
        }
        var pos = nameStart + qualified.Length;
        var typeArgs = string.Empty;
        if (pos < text.Length && text[pos] == '<')
        {
            var gt = FindMatchingAngle(text, pos);
            if (gt < 0)
            {
                return false;
            }
            typeArgs = text[(pos + 1)..gt];
            pos = gt + 1;
        }
        if (pos >= text.Length || text[pos] != ')')
        {
            return false;
        }
        if (pos + 1 >= text.Length || text[pos + 1] != '(')
        {
            return false;
        }
        var innerOpen = pos + 1;
        var innerClose = TsGenericErasureIrRewriter.FindMatchingCloseParen(text, innerOpen);
        if (innerClose < 0)
        {
            return false;
        }
        var simple = SimpleName(qualified);
        if (!_interfaces.ContainsKey(simple))
        {
            return false;
        }
        castInner = text[(innerOpen + 1)..innerClose].Trim();
        castType = typeArgs.Length > 0 ? $"{simple}<{MapTypeArgs(typeArgs)}>" : simple;
        consumed = innerClose + 1 - i;
        return true;
    }

    /// <summary>泛型实参 Java 名 → TS 名（String → string 等；未命中保留原样）。</summary>
    private static string MapTypeArgs(string javaArgs)
    {
        return string.Join(", ", SplitTopLevel(javaArgs).Select(MapTypeArg));
    }

    private static string MapTypeArg(string arg)
    {
        var trimmed = arg.Trim();
        var lt = trimmed.IndexOf('<');
        var head = lt >= 0 ? trimmed[..lt] : trimmed;
        var tail = lt >= 0 ? trimmed[lt..] : string.Empty;
        var simple = SimpleName(head);
        return s_tsTypeArgMap.GetValueOrDefault(simple, simple) + tail;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(text[start..i]);
                    start = i + 1;
                    break;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static int FindMatchingAngle(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i = TsGenericErasureIrRewriter.SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            if (ch == '<')
            {
                depth++;
            }
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>读取 {ident}(.{ident})* 限定名（用于 Outer.IFoo 这类嵌套接口引用）。</summary>
    private static string ReadQualifiedName(string text, int start)
    {
        var part = TsGenericErasureIrRewriter.ReadIdentifier(text, start);
        if (part.Length == 0)
        {
            return string.Empty;
        }
        var pos = start + part.Length;
        while (pos < text.Length && text[pos] == '.')
        {
            var next = TsGenericErasureIrRewriter.ReadIdentifier(text, pos + 1);
            if (next.Length == 0)
            {
                break;
            }
            part += "." + next;
            pos += 1 + next.Length;
        }
        return part;
    }

    private static string SimpleName(string javaName)
    {
        var lastDot = javaName.LastIndexOf('.');
        return lastDot >= 0 ? javaName[(lastDot + 1)..] : javaName;
    }

    private static string StripTypeArgs(string javaType)
    {
        var lt = javaType.IndexOf('<');
        return lt >= 0 ? javaType[..lt] : javaType;
    }

    private void Warn(string message)
    {
        if (_diagnosed.Add(message))
        {
            _context.Diagnostics.Warning(message);
        }
    }
}
