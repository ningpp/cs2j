using System.Text;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 最小 raw 语句归一化（M1，Task 11 引入，计划外必需项；Task 13 扩展）。
/// 前端对 Console.WriteLine/foreach/经典 for/return 字面量等不产出结构化节点，IR 中为
/// JavaRawStatement 整段直通（如 "System.out.println(""hello"");"、"for (int item : arr) {...}"、
/// "for (int i = 0; i < n; i = i + 1) {...}"、"return 'A';"）。分叉后 TS 输出要合法且计划
/// 断言可转绿，发射前做最小文本归一化：
/// ① System.out.println → console.log、System.err.println → console.error（保留实参与分号）；
/// ② Java 增强 for 头 "for (Type var : expr)" → "for (const var of expr)"（Type 丢弃、const
///    推断；expr 与 body 原样保留）；经典 for 头声明段 "for (TYPE var = init; ...)" →
///    "for (let var: MappedType = init; ...)"（MappedType 走 TsMemberMappingTable，经
///    mapType 回调注入；仅当声明段匹配简单 "TYPE identifier = expr" 形态时改写，多声明/
///    无初始化/泛型数组类型/映射未命中/for (;;) 直通）。两条规则按头内是否含顶层 ";" 区分
///    （经典 for 含 ";"，增强 for 不含）。
/// ③ char 字面量 'x' → 双引号字符串 "x"（与 char→string 映射语义一致；含转义处理
///    '\n'→"\n"、'\''→"'"；内含双引号的 '\"' 保持单引号直通，M2 字符处理任务细化）。
/// 不匹配时原样直通。
/// Task 11 复审修复（2026-08-07）：
/// - 改写/替换均跳过字符串/字符字面量区域且只在标识符边界命中（实参含 "System.out.println("
///   文本的字符串字面量不被改写）；
/// - 增强 for 头增加顶层 ";" 守卫（普通 for 含任意三元，含标识符操作数，一律直通）；
/// - 同一 raw 文本内所有增强 for 头均归一化（嵌套 foreach 内外层都改写，body 原样保留）。
/// Task 13（2026-08-07）：经典 for 头声明段 let 化 + char 字面量归一化（见 ②③）。
/// 表达式层重写、IR 结构化、S 级 rewriter 均属 M2+，本类不做。
/// </summary>
public static class TsRawStatementNormalizer
{
    /// <summary>
    /// 泛型后缀（平衡尖括号）匹配模式：<T> / Set<SymmetricTuple<Point>> 必须停在
    /// 类型自身的闭合 >。旧 (?:<[^;\r\n]*>)? 会吞掉声明名与初始化器里 lambda => 的
    /// >（"RBNode<ConeSide> rbNode = f(s => ...)" 被吞成类型的一部分），导致 let 化
    /// 失败。用 .NET 平衡组按深度匹配嵌套泛型。
    /// </summary>
    private const string GenericTypeSuffixPattern =
        @"(?:<(?:(?<Open><)|(?<-Open>>)|[^<>\r\n;])*>(?(Open)(?!)))";

    private const string OutPrintln = "System.out.println(";
    private const string ErrPrintln = "System.err.println(";

    // 与 TsStatementWriter.EmitFor 同款：long 字面量 L 后缀 → bigint n 后缀（raw 文本形态）。
    private static readonly Regex LongLiteralPattern = new(@"(0[xX][0-9a-fA-F_]+|0[bB][01_]+|\d[\d_]*)[lL]");

    public static string Normalize(string code)
    {
        return Normalize(code, null);
    }

    /// <summary>
    /// 归一化 raw 语句文本；mapType 为 Java→TS 类型映射器（TsExpressionWriter.MapType），
    /// 供经典 for 头声明段 let 化使用；为 null 时经典 for 仅保留增强 for 行为（直通）。
    /// mapException 为异常名映射回调（Task 21：TsExpressionWriter.MapExceptionName），
    /// 将 raw 文本中的 Java 风味异常名（new IllegalStateException( 等）替换为兼容库名；
    /// 为 null 时跳过异常名归一化。
    /// </summary>
    public static string Normalize(string code, Func<string, string>? mapType = null, Func<string, string?>? mapException = null, bool normalizeChars = true)
    {
        if (string.IsNullOrEmpty(code))
        {
            return code;
        }

        // ① println/err.println → console.log/console.error。
        // 扫描替换：跳过字符串/字符字面量区域（实参含 "System.out.println(" 文本时不被改写），
        // 仅在标识符边界命中；WriteLineIf 产生的 "if (cond) System.out.println(x);" 同样命中。
        var normalized = NormalizePrintln(code);

        // ② Java for 头归一化（同一文本内所有头，含嵌套；增强 for → for-of，经典 for 声明段
        // let 化；不匹配则直通）。
        var withFors = NormalizeForHeads(normalized, mapType);

        // ③ char 字面量 → 双引号字符串（raw 语句中 return 'A'; 等直通文本的归一化）。
        // Expression contexts (raw initializer/argument text) keep single-quoted char
        // literals: they are valid TS strings and the char->string rewrite would change
        // established output shapes (CsString.split(s, ',')).
        var withChars = normalizeChars ? NormalizeCharLiterals(withFors) : withFors;

        // ④ Java 风味异常名 → 兼容库名（Task 21：方法体直通文本中的 throw new X(...) /
        // instanceof X；结构化 new/instanceof/catch 由发射器在节点层映射）。
        var withExceptions = NormalizeExceptionNames(withChars, mapException);

        // ⑤ long 字面量 L 后缀 → n 后缀（Task 22 冒烟：raw 文本中 7L → 7n；数值语义
        // rewriter 改写出的 BigInt(...) 混合链同样落在 raw 文本，必须归一化）。
        var withLongs = NormalizeLongLiterals(withExceptions);

        // ⑥ EventArgs.Empty 的 Java 化产物 Object.Empty → null（Task 27 事件：
        // C# EventHandler 触发 Click?.Invoke(this, EventArgs.Empty) 中 EventArgs
        // 映射为 Object，TS 无 Object.Empty 成员；空事件参数统一 null）
        var withObjectEmpty = NormalizeObjectEmpty(withLongs);

        // ⑦ Java 形态局部声明 "Type name = init;" → "let name: MappedType = init;"
        //（Task 27 模式匹配：前端对含 is 模式的条件整段 raw 发射 if，段首声明落在
        // raw 文本；TS 无 "Type name" 声明语法）。仅命中行首 "类型 标识符 = ...;"
        // 且类型为单个标识符/泛型名时改写（b.R = 5 等成员赋值因类型后跟 "." 不命中）。
        // 6b Java method-group references (::) and lambda arrows (->): TS has neither
        // syntax; arrow functions preserve delegate-call semantics.
        var withMethodRefs = NormalizeMethodReferences(withObjectEmpty);

        // 6c Java control-flow text: typed catch bindings, try-with-resources heads,
        // synchronized blocks, double literal suffixes, explicit generic calls.
        var withCatch = NormalizeCatchClauses(withMethodRefs);
        var withTryResources = NormalizeTryWithResources(withCatch);
        var withSync = NormalizeSynchronizedBlocks(withTryResources);
        var withDoubles = NormalizeDoubleSuffixLiterals(withSync);
        var withGenericCalls = NormalizeGenericMethodInvocations(withDoubles);

        var withDecls = NormalizeRawDeclarations(withGenericCalls, mapType);

        // ⑧ Java 形态空安全相等调用 java.util.Objects.equals(a, b) / Objects.equals(a, b)
        // → a === b（Task 27 模式匹配：switch 语句常量标签 case null:/case 5: 的 Java 化
        // 条件 java.util.Objects.equals(o, null) 无 TS 对应；字符串/引用相等按 Task 26
        // 语义统一 ===）。
                var withEquals = NormalizeObjectsEquals(withDecls);
        // ? Java float literal suffix (2.0f/1.5F) -> dropped (TS has no f suffix;
        // matches EmitLiteral semantics).
        var withFloats = NormalizeFloatLiterals(withEquals);

        // ? Java array creation/initializer literals (new String[] { "a", "b" } /
        // new double[][] { {...} }) -> TS array literals (["a", "b"] / [[...]]);
        // shared by raw method bodies and field initializers.
        var withArrays = NormalizeArrayLiterals(withFloats);

        // ? Java reference casts ((Iterable<Edge>)(Iterable<?>)(expr), (Cluster) x,
        // (Map.Entry<K,V>) expr) -> dropped: TS has no cast syntax and reference
        // casts carry no runtime semantics (the value is already the target type).
        // Primitive casts ((int) x / (double) x) keep numeric semantics and stay.
        var withCasts = NormalizeReferenceCasts(withArrays);

        // ? Project FQCNs in expression positions (new Microsoft.Msagl.Core.Geometry.Point(...),
        // Microsoft.Msagl.Core.Geometry.Point.op_Add(...)) -> last segment via mapType
        // (matches the type-position shortening; imports are collected by short name).
        return NormalizeFqcnExpressions(withCasts, mapType);
    }


    /// <summary>
    /// When code[i] starts a comment (// or /*), copies it verbatim to sb and
    /// returns the index after the comment; otherwise returns i unchanged.
    /// Line comments stop before the trailing newline (the caller copies it).
    /// </summary>
    private static int SkipCommentTo(System.Text.StringBuilder sb, string code, int i)
    {
        if (i + 1 >= code.Length || code[i] != '/')
        {
            return i;
        }
        if (code[i + 1] == '/')
        {
            var nl = code.IndexOf('\n', i + 2);
            var end = nl < 0 ? code.Length : nl;
            sb.Append(code, i, end - i);
            return end;
        }
        if (code[i + 1] == '*')
        {
            var close = code.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
            var end = close < 0 ? code.Length : close + 2;
            sb.Append(code, i, end - i);
            return end;
        }
        return i;
    }

    /// <summary>
    /// 扫描替换 println 接收者；引号内文本与实参原样保留。
    /// </summary>
    private static string NormalizePrintln(string code)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }

            if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith(OutPrintln, StringComparison.Ordinal))
            {
                sb.Append("console.log(");
                i += OutPrintln.Length;
                continue;
            }
            if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith(ErrPrintln, StringComparison.Ordinal))
            {
                sb.Append("console.error(");
                i += ErrPrintln.Length;
                continue;
            }

            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// "for (Type var : expr) ..." → "for (const var of expr) ..."；
    /// "for (Type var = init; cond; inc) ..." → "for (let var: TsType = init; cond; inc) ..."。
    /// 扫描同一文本内所有 "for (" 头（含嵌套 foreach 的 body 内层头），逐个改写；
    /// 其余文本原样保留。
    /// </summary>
    private static string NormalizeForHeads(string code, Func<string, string>? mapType)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }

            if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith("for (", StringComparison.Ordinal))
            {
                var close = FindMatchingParen(code, i + 4);
                if (close >= 0)
                {
                    // 头内是否含顶层 ";" 区分增强 for 与经典 for（③ 规则见类注释）
                    if (TryRewriteForHead(code, i, close, out var forOfHead))
                    {
                        sb.Append(forOfHead);
                        i = close + 1;
                        continue;
                    }
                    if (TryRewriteClassicForHead(code, i, close, mapType, out var classicHead))
                    {
                        sb.Append(classicHead);
                        i = close + 1;
                        continue;
                    }
                }
            }

            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 尝试把 [openIndex, closeIndex] 范围内的 for 头改写为 for-of 头；不匹配返回 false。
    /// 头内 " : " 之前的部分必须形如 "Type var"（末 token 为标识符），且头内无顶层 ";"。
    /// </summary>
    private static bool TryRewriteForHead(string code, int openIndex, int closeIndex, out string head)
    {
        head = string.Empty;
        var header = code.Substring(openIndex + 5, closeIndex - openIndex - 5);
        var sepIndex = header.IndexOf(" : ", StringComparison.Ordinal);
        if (sepIndex < 0)
        {
            return false;
        }

        // 左侧 "Type var"：Type 丢弃（const 推断），变量名取末 token（如 "Map.Entry<K, V> entry"）。
        var left = header.Substring(0, sepIndex).Trim();
        var right = header.Substring(sepIndex + 3).Trim();
        var varName = ExtractVariableName(left);
        if (varName == null || !IsIdentifier(varName))
        {
            return false;
        }

        // 普通 for（含头内任意三元，如 flag ? a : b）含顶层 ";"，必须直通。
        if (ContainsTopLevelSemicolon(header))
        {
            return false;
        }

        head = $"for (const {varName} of {StripErasureCasts(right)})";
        return true;
    }

    /// <summary>
    /// Strips Java erasure casts wrapping a collection expression
    /// ((Iterable&lt;Edge&gt;)(Iterable&lt;?&gt;)(expr) -> (expr)): TS has no erasure
    /// cast syntax and the cast carries no runtime semantics, so it is dropped.
    /// </summary>
    private static string StripErasureCasts(string expr)
    {
        var result = expr;
        while (true)
        {
            var m = Regex.Match(result, @"^\(\s*(?:Iterable|CSharpGenericIterable)(?:<[^>]*>)?\s*\)\s*");
            if (!m.Success)
            {
                return result;
            }
            result = result[m.Length..];
        }
    }

    /// <summary>
    /// 尝试把 [openIndex, closeIndex] 范围内的经典 for 头声明段 let 化：
    /// "for (TYPE var = init; ...)" → "for (let var: TsType = init; ...)"。
    /// 仅当声明段匹配简单 "TYPE identifier = expr" 形态且类型经 mapType 映射命中时改写；
    /// 多声明（init 顶层逗号）、无初始化、泛型/数组类型、映射未命中、for (;;) 一律直通。
    /// </summary>
    private static bool TryRewriteClassicForHead(string code, int openIndex, int closeIndex, Func<string, string>? mapType, out string head)
    {
        head = string.Empty;
        if (mapType == null)
        {
            return false;
        }

        var header = code.Substring(openIndex + 5, closeIndex - openIndex - 5);
        var semicolons = FindTopLevelSemicolons(header);
        if (semicolons.Count < 2)
        {
            // 增强 for（无顶层 ";"）或 for (;;)（三段均为空）不应进入声明段改写
            return false;
        }

        var initPart = header.Substring(0, semicolons[0]).Trim();
        var condPart = header.Substring(semicolons[0] + 1, semicolons[1] - semicolons[0] - 1).Trim();
        var incPart = header.Substring(semicolons[1] + 1).Trim();

        // Declaration section = "TYPE name = init[, name2 = init2 ...]"; the type is
        // an identifier (optionally dotted/generic/array suffix).
        var m = Regex.Match(initPart, $@"^(?:final\s+)?(?<type>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*{GenericTypeSuffixPattern}?(?:\[\])*)\s+(?<decls>.+)$");
        if (!m.Success)
        {
            return false;   // no-initializer etc. pass through
        }
        var javaType = m.Groups["type"].Value.Trim();
        if (!TryRewriteDeclarators(m.Groups["decls"].Value, javaType, mapType, out var declarators))
        {
            return false;
        }
        head = $"for (let {declarators}; {condPart}; {incPart})";
        return true;
    }

    /// <summary>
    /// Rewrites a declarator list "name = init[, name2 = init2 ...]" into a TS let
    /// declarator list ("name: T = init, name2: T = init2") with the type mapped via
    /// mapType; class-like unmapped user types (Rail/PolylinePoint) keep their name.
    /// Returns false when the shape does not match.
    /// </summary>
    private static bool TryRewriteDeclarators(string decls, string javaType, Func<string, string>? mapType, out string rewritten)
    {
        rewritten = string.Empty;
        if (mapType == null)
        {
            return false;
        }
        var tsType = mapType(javaType);
        // Identity mappings are accepted for TS keywords (boolean/void/...) and
        // class-like names; only unknown lowercase names are rejected.
        if (tsType == javaType && !LooksLikeClassType(javaType)
            && javaType is not ("boolean" or "void" or "object" or "any" or "unknown" or "never" or "string" or "number"))
        {
            return false;
        }
        var parts = new List<string>();
        foreach (var declarator in SplitTopLevelCommas(decls))
        {
            var dm = Regex.Match(declarator, @"^(?<name>[A-Za-z_$][\w$]*)\s*(?:=\s*(?<init>.+))?$");
            if (!dm.Success)
            {
                return false;
            }
            var name = TsNaming.SafeIdentifier(dm.Groups["name"].Value);
            parts.Add(dm.Groups["init"].Success
                ? $"{name}: {tsType} = {dm.Groups["init"].Value.Trim()}"
                : $"{name}: {tsType}");
        }
        rewritten = string.Join(", ", parts);
        return true;
    }

    /// <summary>
    /// 将 raw 语句文本中的 char 字面量 'x' 归一化为双引号字符串 "x"。
    /// 跳过双引号字符串区域；仅当单引号内容为单字符或转义序列时改写（转义 '\'' 去转义为
    /// "'"，其余转义原样保留——"\n"/"\t"/"\\" 等在双引号字符串中均合法）；
    /// 内含双引号的 '\"' 保持单引号直通（M2 字符处理任务细化）。
    /// </summary>
    private static string NormalizeCharLiterals(string code)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }

            if (ch == '\'')
            {
                var close = FindClosingQuote(code, i);
                if (close >= 0 && TryRewriteCharLiteral(code, i, close, out var rewritten))
                {
                    sb.Append(rewritten);
                    i = close + 1;
                    continue;
                }
            }

            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 尝试把 [openIndex, closeIndex] 范围内的单引号字面量改写为双引号字符串；不匹配返回 false。
    /// </summary>
    private static bool TryRewriteCharLiteral(string code, int openIndex, int closeIndex, out string rewritten)
    {
        rewritten = string.Empty;
        var inner = code.Substring(openIndex + 1, closeIndex - openIndex - 1);
        if (inner.Length == 0 || inner.Contains('"'))
        {
            return false;
        }
        if (inner.Length > 1 && inner[0] != '\\')
        {
            return false;   // 多字符非转义内容：非 char 字面量形态，直通
        }
        rewritten = "\"" + inner.Replace("\\'", "'") + "\"";
        return true;
    }

    /// <summary>
    /// 将 raw 语句文本中的 long 字面量 L 后缀归一化为 bigint n 后缀（Task 22）。
    /// 跳过字符串/字符字面量区域；仅在标识符边界命中数字起点（避免误改
    /// abc123L 这类标识符中的数字段）；0x/0b 前缀与下划线分隔均覆盖。
    /// </summary>
    /// <summary>
    /// 扫描替换 Object.Empty（EventArgs.Empty 的 Java 化形态）→ null；跳过字符串/字符字面量。
    /// </summary>
    /// <summary>
    /// Normalizes Java method-group references and lambda arrows in raw text:
    /// "this::pack" -> "(...args) => this.pack(...args)",
    /// "Integer::intValue" -> "(x) => Number(x)", "Block[]::new" -> "(n) => new Array(n)",
    /// "n -> n.getBoundingBox()" -> "n => n.getBoundingBox()". Arrow functions keep
    /// delegate-call semantics; boxed-primitive accessors and Runnable.run get
    /// element-wise rewrites. Skips string/char literals.
    /// </summary>
    private static string NormalizeMethodReferences(string code)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }
            if (ch == ':' && i + 1 < code.Length && code[i + 1] == ':'
                && TryRewriteMethodReference(code, i, out var rewritten, out var consumed, out var receiverStart))
            {
                // Drop the receiver text already copied to the buffer: the rewrite
                // re-emits it inside the arrow function.
                sb.Length -= i - receiverStart;
                sb.Append(rewritten);
                i += consumed;
                continue;
            }
            if (ch == '-' && i + 1 < code.Length && code[i + 1] == '>')
            {
                // Java lambda arrow: "n -> expr" / "(a, b) -> expr" / "x->expr".
                // Preceding char is ')' , an identifier char, or whitespace.
                var prev = i > 0 ? code[i - 1] : '\0';
                if (prev == ')' || IsIdentifierChar(prev) || char.IsWhiteSpace(prev))
                {
                    sb.Append("=>");
                    i += 2;
                    continue;
                }
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Rewrites a "::" method-group reference at position at (the first colon).
    /// Returns the replacement text and how many source chars it consumed
    /// (the "::" plus the trailing member name; receiver is kept in the output).
    /// </summary>
    private static bool TryRewriteMethodReference(string code, int at, out string rewritten, out int consumed, out int receiverStart)
    {
        rewritten = string.Empty;
        consumed = 2;
        receiverStart = at;
        var recvStart = at - 1;
        if (recvStart >= 1 && code[recvStart] == ']' && code[recvStart - 1] == '[')
        {
            // "Type[]::new": step back over the "[]" suffix before scanning the type name.
            recvStart -= 2;
        }
        while (recvStart >= 0 && IsIdentifierChar(code[recvStart]))
        {
            recvStart--;
        }
        recvStart++;
        receiverStart = recvStart;
        var recvLen = at - recvStart;
        if (recvLen == 0)
        {
            return false;
        }
        var recv = code.Substring(recvStart, recvLen);

        var after = at + 2;
        var member = ReadIdentifier(code, after);
        if (member.Length == 0)
        {
            return false;
        }

        if (recv.EndsWith("[]", StringComparison.Ordinal))
        {
            // Type[]::new -> (n) => new Array(n): array constructor reference used
            // by .toArray(X[]::new).
            if (member != "new")
            {
                return false;
            }
            rewritten = "(n) => new Array(n)";
            consumed = 2 + member.Length;
            return true;
        }

        if (!IsIdentifier(recv) && recv != "this")
        {
            return false;
        }

        var boxedNumeric = recv is "Integer" or "Double" or "Float" or "Long" or "Short";
        var numericAccessor = member is "intValue" or "doubleValue" or "floatValue" or "longValue" or "shortValue";
        if (boxedNumeric && numericAccessor)
        {
            rewritten = "(x) => Number(x)";
            consumed = 2 + member.Length;
            return true;
        }
        if (recv == "Boolean" && member == "booleanValue")
        {
            rewritten = "(x) => Boolean(x)";
            consumed = 2 + member.Length;
            return true;
        }
        if (recv == "Runnable" && member == "run")
        {
            // Element-wise call: actions.forEach(Runnable::run) -> (x) => x.run().
            rewritten = "(x) => x.run()";
            consumed = 2 + member.Length;
            return true;
        }

        rewritten = $"(...args) => {recv}.{member}(...args)";
        consumed = 2 + member.Length;
        return true;
    }

    /// <summary>Reads an identifier starting at index; returns empty when absent.</summary>
    private static string ReadIdentifier(string code, int at)
    {
        if (at >= code.Length || !IsIdentifierStart(code[at]))
        {
            return string.Empty;
        }
        var end = at + 1;
        while (end < code.Length && IsIdentifierChar(code[end]))
        {
            end++;
        }
        return code.Substring(at, end - at);
    }

    private static bool IsIdentifierStart(char c)
        => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or '$';

    /// <summary>
    /// "catch (Exception __e)" -> "catch (__e)": TS catch bindings carry no type
    /// annotation. Multi-catch unions ("catch (A | B e)") also lose their types.
    /// </summary>
    private static string NormalizeCatchClauses(string code)
    {
        var catchPattern = new Regex(@"\bcatch\s*\(\s*(?<types>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*(?:\s*[|&]\s*[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)*)\s+(?<name>[A-Za-z_$][\w$]*)\s*\)");
        return catchPattern.Replace(code, match => $"catch ({match.Groups["name"].Value})");
    }

    /// <summary>
    /// Java try-with-resources "try (Type name = init) {" -> "try { let name = init;":
    /// the resource declaration becomes a local (TS has no resource syntax).
    /// Resources separated by ";" each become their own let declaration.
    /// </summary>
    private static string NormalizeTryWithResources(string code)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }
            if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith("try (", StringComparison.Ordinal))
            {
                var close = FindMatchingParen(code, i + 4);
                if (close >= 0 && TryRewriteTryHead(code, i + 4, close, out var rewritten))
                {
                    var after = close + 1;
                    while (after < code.Length && char.IsWhiteSpace(code[after]))
                    {
                        after++;
                    }
                    if (after < code.Length && code[after] == '{')
                    {
                        sb.Append(rewritten);
                        i = after + 1;   // body continues after its opening '{'
                        continue;
                    }
                }
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Parses a try-with-resources head into let declarations; false when
    /// any resource does not match "Type name [= init]".</summary>
    private static bool TryRewriteTryHead(string code, int openIndex, int closeIndex, out string rewritten)
    {
        rewritten = string.Empty;
        var head = code.Substring(openIndex + 1, closeIndex - openIndex - 1);
        if (string.IsNullOrWhiteSpace(head))
        {
            return false;
        }
        var resources = SplitTopLevelSemicolons(head);
        var decls = new List<string>();
        foreach (var resource in resources)
        {
            var trimmed = resource.Trim();
            var m = Regex.Match(trimmed, @"^(?:final\s+)?[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*(?:<[^;]*>)?(?:\s*\[\s*\])*\s+(?<name>[A-Za-z_$][\w$]*)\s*(?:=\s*(?<init>.+))?$");
            if (!m.Success)
            {
                return false;
            }
            var name = m.Groups["name"].Value;
            decls.Add(m.Groups["init"].Success
                ? $"let {name} = {m.Groups["init"].Value.Trim()}"
                : $"let {name}");
        }
        rewritten = $"try {{ {string.Join("; ", decls)};";
        return true;
    }

    /// <summary>Splits text on top-level semicolons (outside parens/brackets/braces/
    /// generics and quoted literals).</summary>
    private static List<string> SplitTopLevelSemicolons(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var startIdx = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            switch (c)
            {
                case '(':
                case '[':
                case '{':
                case '<':
                    depth++;
                    break;
                case ')':
                case ']':
                case '}':
                case '>':
                    depth--;
                    break;
                default:
                    if (c == ';' && depth == 0)
                    {
                        result.Add(text[startIdx..i].Trim());
                        startIdx = i + 1;
                    }
                    break;
            }
        }
        result.Add(text[startIdx..].Trim());
        return result;
    }

    /// <summary>
    /// "synchronized (this) {" -> "{": TS has no monitor syntax; the block body is
    /// kept (lock semantics are a documented known limitation).
    /// </summary>
    private static string NormalizeSynchronizedBlocks(string code)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }
            if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith("synchronized", StringComparison.Ordinal))
            {
                var j = i + "synchronized".Length;
                while (j < code.Length && char.IsWhiteSpace(code[j]))
                {
                    j++;
                }
                if (j < code.Length && code[j] == '(')
                {
                    var close = FindMatchingParen(code, j);
                    if (close >= 0)
                    {
                        var after = close + 1;
                        while (after < code.Length && char.IsWhiteSpace(code[after]))
                        {
                            after++;
                        }
                        if (after < code.Length && code[after] == '{')
                        {
                            i = after;   // keep the '{' and everything after
                            continue;
                        }
                    }
                }
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Java double literal suffix: 0.0d/1e-3D -> 0.0/1e-3 (TS has no d suffix).
    /// Skips strings, chars and comments.
    /// </summary>
    private static string NormalizeDoubleSuffixLiterals(string code)
    {
        var doublePattern = new Regex(@"(?<![A-Za-z_$0-9])(?<num>\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)[dD](?![A-Za-z_$0-9])");
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }
            var m = doublePattern.Match(code, i);
            if (m.Success && m.Index == i)
            {
                sb.Append(m.Groups["num"].Value);
                i += m.Length;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Java explicit generic method invocation "Collections.&lt;EdgeGeometry&gt;emptyList()"
    /// -> "Collections.emptyList()": TS infers type arguments at runtime.
    /// </summary>
    private static string NormalizeGenericMethodInvocations(string code)
    {
        var genericCallPattern = new Regex(@"\.<[^<>]*>[A-Za-z_$][\w$]*\(");
        return genericCallPattern.Replace(code, match =>
        {
            var afterTypeArgs = match.Value.IndexOf('>');
            return "." + match.Value[(afterTypeArgs + 1)..];
        });
    }

    /// <summary>
    /// Java 形态局部声明归一化（Task 27 模式匹配）：行首 "Type name = init;" →
    /// "let name: MappedType = init;"。类型仅匹配单标识符或泛型名（不含 "."，避免
    /// 成员赋值误判）；类型映射回调为空时直通。非声明行（表达式/控制流）不受影响。
    /// </summary>
    private static string NormalizeRawDeclarations(string code, Func<string, string>? mapType)
    {
        if (mapType == null)
        {
            return code;
        }
        var declPattern = new Regex(
            $@"^(?<ind>\s*)(?<type>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*{GenericTypeSuffixPattern}?(?:\[\])*)\s+(?<decls>.+);(?<tail>\s*(?://[^\r\n]*)?)$",
            RegexOptions.Compiled | RegexOptions.Multiline);
        return declPattern.Replace(code, match =>
        {
            var ind = match.Groups["ind"].Value;
            var type = match.Groups["type"].Value;
            var decls = match.Groups["decls"].Value;
            var tail = match.Groups["tail"].Value;
            if (!TryRewriteDeclarators(decls, type, mapType, out var declarators))
            {
                return match.Value;
            }
            return $"{ind}let {declarators};{tail}";
        });
    }

    /// <summary>
    /// Drops Java reference-type casts in raw text: "(Type) expr" -> "expr" for any
    /// non-primitive Type (generic, dotted, array or plain class names). Primitive
    /// casts (int/long/short/byte/char/float/double/boolean) are preserved because
    /// they carry C# numeric conversion semantics. Skips string/char literals.
    /// </summary>
    private static string NormalizeReferenceCasts(string code)
    {
        var castPattern = new Regex(@"\(\s*(?<type>[A-Z_$][\w$]*(?:\.[A-Z_$][\w$]*)*(?:<[^>]*>)?(?:\[\])*)\s*\)\s*(?=[A-Za-z_(])");
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }
            if (ch == '(')
            {
                var m = castPattern.Match(code, i);
                if (m.Success && m.Index == i && !IsPrimitiveType(m.Groups["type"].Value)
                    && !IsControlFlowCondition(code, i)
                    && !IsFollowedByStatementKeyword(code, m.Index + m.Length))
                {
                    i += m.Length;
                    continue;
                }
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsPrimitiveType(string type)
        => type is "int" or "long" or "short" or "byte" or "sbyte" or "char"
            or "float" or "double" or "boolean";

    /// <summary>
    /// True when the '(' at openParen opens the parenthesized condition of a
    /// control-flow keyword (if/while/switch/for/catch/synchronized). "(X)" there
    /// is a parenthesized condition expression, never a cast — treating it as a
    /// cast would drop the condition (e.g. "if (_iteratorHasNext) return true;"
    /// would become "if return true;").
    /// </summary>
    private static bool IsControlFlowCondition(string code, int openParen)
    {
        var j = openParen - 1;
        while (j >= 0 && char.IsWhiteSpace(code[j]))
        {
            j--;
        }
        var end = j + 1;
        while (j >= 0 && IsIdentifierChar(code[j]))
        {
            j--;
        }
        var word = code.Substring(j + 1, end - j - 1);
        return word is "if" or "while" or "switch" or "for" or "catch" or "synchronized";
    }

    /// <summary>
    /// True when the text right after a candidate cast starts with a statement
    /// keyword (return/throw/...): "(Type) return ..." is a parenthesized
    /// condition followed by a statement, never a cast expression.
    /// </summary>
    private static bool IsFollowedByStatementKeyword(string code, int at)
    {
        var j = at;
        while (j < code.Length && char.IsWhiteSpace(code[j]))
        {
            j++;
        }
        if (j >= code.Length || !IsIdentifierStart(code[j]))
        {
            return false;
        }
        var end = j + 1;
        while (end < code.Length && IsIdentifierChar(code[end]))
        {
            end++;
        }
        var word = code.Substring(j, end - j);
        return word is "return" or "throw" or "if" or "for" or "while" or "switch"
            or "do" or "else" or "case" or "break" or "continue" or "try"
            or "catch" or "finally" or "synchronized" or "assert" or "yield" or "goto";
    }

    /// <summary>
    /// Shortens project FQCNs in expression positions to their last segment:
    /// "new Microsoft.Msagl.Core.Geometry.Point(...)" -> "new Point(...)",
    /// "Microsoft.Msagl.Core.Geometry.Point.op_Add(a, b)" -> "Point.op_Add(a, b)".
    /// Only chains whose first segment starts with an uppercase letter are
    /// considered (this/self/lowercase member chains are never touched); the mapType
    /// oracle guarantees the prefix is a known type (identity mappings are skipped).
    /// </summary>
    private static string NormalizeFqcnExpressions(string code, Func<string, string>? mapType)
    {
        if (mapType == null)
        {
            return code;
        }
        var chainPattern = new Regex(@"(?<prefix>\b[A-Z][A-Za-z0-9_$]*(?:\.[A-Za-z_$][\w$]*)+)");
        return chainPattern.Replace(code, match =>
        {
            var full = match.Groups["prefix"].Value;
            var after = match.Index + match.Length < code.Length ? code[match.Index + match.Length] : '\0';
            var before = match.Index > 0 ? code[match.Index - 1] : '\0';
            if (before == '\'' || before == '"' || before == '\'')
            {
                return full;
            }
            if (after == '(')
            {
                // Creation-type position (preceded by "new ") or generic call: only
                // shorten when preceded by "new ".
                if (match.Index >= 4 && code.Substring(match.Index - 4, 4) == "new ")
                {
                    var mapped = mapType(full);
                    return mapped == full ? full : mapped;
                }
                return full;
            }
            if (after == '.')
            {
                // Static/member call: shorten the type prefix (all segments before
                // the final member), never the member itself.
                var lastDot = full.LastIndexOf('.');
                var prefix = full[..lastDot];
                var mapped = mapType(prefix);
                return mapped == prefix ? full : mapped + full[lastDot..];
            }
            return full;
        });
    }

    /// <summary>?????????????????????/???/?????</summary>
    private static List<string> SplitTopLevelCommas(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var startIdx = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            switch (c)
            {
                case '(':
                case '[':
                case '{':
                case '<':
                    depth++;
                    break;
                case ')':
                case ']':
                case '}':
                case '>':
                    depth--;
                    break;
                default:
                    if (c == ',' && depth == 0)
                    {
                        result.Add(text[startIdx..i].Trim());
                        startIdx = i + 1;
                    }
                    break;
            }
        }
        result.Add(text[startIdx..].Trim());
        return result;
    }

    /// <summary>
    /// Java float literal suffix normalization: 2.0f/1.5F -> 2.0/1.5 (TS has no f
    /// suffix; matches EmitLiteral semantics). Skips string/char literals and only
    /// fires at identifier boundaries from a digit start.
    /// </summary>
    private static string NormalizeFloatLiterals(string code)
    {
        var floatPattern = new Regex(@"(?<num>\d[\d_]*(?:\.\d[\d_]*)?)[fF](?![\w$])");
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }
            if (char.IsDigit(ch))
            {
                var m = floatPattern.Match(code, i);
                if (m.Success && m.Index == i)
                {
                    sb.Append(m.Groups["num"].Value);
                    i += m.Length;
                    continue;
                }
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Java array creation literal normalization: "new String[] { "a", "b" }" ->
    /// "["a", "b"]", "new double[][] { { 1, 2 }, ... }" -> "[[1, 2], ...]".
    /// Braces inside the initializer become brackets; the "new TYPE[]" prefix is
    /// dropped (TS array literals carry no type). Skips string/char literals.
    /// </summary>
    private static string NormalizeArrayLiterals(string code)
    {
        var arrayPattern = new Regex(@"new\s+[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*(?:<[^>]*>)?(?:\[\])*\s*(?<body>\{[^\r\n]*\})\s*");
        var withBraces = arrayPattern.Replace(code, match =>
        {
            var body = match.Groups["body"].Value;
            var sb = new StringBuilder(body.Length);
            var inString = false;
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '"')
                {
                    inString = !inString;
                    sb.Append(c);
                    continue;
                }
                sb.Append(c == '{' ? '[' : c == '}' ? ']' : c);
            }
            return sb.ToString();
        });

        // Java dimension-only array creations: "new double[2][]" / "new T[n]" ->
        // "new Array(n)" (TS has no dimension syntax; Array(n) yields the same shape).
        var arrayDimPattern = new Regex(@"new\s+[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*(?:<[^>]*>)?\[(?<dim>[^\]]+)\](?:\[\])*(?!\s*\{)");
        return arrayDimPattern.Replace(withBraces, match => $"new Array({match.Groups["dim"].Value.Trim()})");
    }

    private static bool LooksLikeClassType(string type)
    {
        // 类型名以大写开头或为泛型形态才视为类声明（小写原始类型名 int/boolean 等
        // 也合法——原始类型映射为 number/boolean 等，mapped != type 时已放行）
        return char.IsUpper(type[0]) || type.Contains('<');
    }

    /// <summary>
    /// 将 raw 语句文本中的 Java 空安全相等调用归一化为 ===（Task 27 模式匹配）：
    /// "java.util.Objects.equals(a, b)" / "Objects.equals(a, b)" → "a === b"。
    /// 跳过字符串/字符字面量区域、仅在标识符边界命中；实参内层
    /// 括号/逗号不受影响（按顶层逗号拆分两实参）。不匹配时原样直通。
    /// </summary>
    private static string NormalizeObjectsEquals(string code)
    {
        const string qualifiedNeedle = "java.util.Objects.equals(";
        const string bareNeedle = "Objects.equals(";
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }

            string? needle = null;
            var needleLength = 0;
            if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith(qualifiedNeedle, StringComparison.Ordinal))
            {
                needle = qualifiedNeedle;
                needleLength = qualifiedNeedle.Length;
            }
            else if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith(bareNeedle, StringComparison.Ordinal))
            {
                needle = bareNeedle;
                needleLength = bareNeedle.Length;
            }
            if (needle != null)
            {
                var open = i + needleLength - 1;
                var close = FindMatchingParen(code, open);
                if (close > open
                    && TrySplitTopLevelArgs(code.Substring(open + 1, close - open - 1), out var left, out var right))
                {
                    sb.Append(left.Trim()).Append(" === ").Append(right.Trim());
                    i = close + 1;
                    continue;
                }
            }

            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>按顶层逗号把实参串拆为左右两段；无顶层逗号返回 false。</summary>
    private static bool TrySplitTopLevelArgs(string args, out string left, out string right)
    {
        left = string.Empty;
        right = string.Empty;
        var depth = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '"' || c == '\'')
            {
                i = SkipQuotedLiteral(args, i) - 1;
                continue;
            }
            switch (c)
            {
                case '(':
                case '[':
                case '{':
                    depth++;
                    break;
                case ')':
                case ']':
                case '}':
                    depth--;
                    break;
                default:
                    if (c == ',' && depth == 0)
                    {
                        left = args[..i];
                        right = args[(i + 1)..];
                        return true;
                    }
                    break;
            }
        }
        return false;
    }

    private static string NormalizeObjectEmpty(string code)
    {
        const string needle = "Object.Empty";
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }
            if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith(needle, StringComparison.Ordinal))
            {
                sb.Append("null");
                i += needle.Length;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static string NormalizeLongLiterals(string code)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }

            if ((char.IsDigit(ch) || ch == '0') && AtIdentifierBoundary(code, i))
            {
                var m = LongLiteralPattern.Match(code, i);
                if (m.Success && m.Index == i)
                {
                    sb.Append(m.Groups[1].Value).Append('n');
                    i += m.Length;
                    continue;
                }
            }

            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 将 raw 语句文本中的 Java 风味异常名替换为 TS 兼容库名（Task 21）。
    /// 两种形态：`new <Name>(`（throw new IllegalStateException(...) 等）与
    /// `instanceof <Name>`。均跳过字符串/字符字面量区域、仅在标识符边界命中；
    /// 回调返回 null（非 BCL 异常/用户类型）时直通。降级名（CsException + 诊断注释）
    /// 由回调拼入，注释位于名字与括号之间（new CsException /* cs2j: unmapped exception */(...)）。
    /// </summary>
    private static string NormalizeExceptionNames(string code, Func<string, string?>? mapException)
    {
        if (mapException == null)
        {
            return code;
        }

        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(code, i);
                sb.Append(code, start, i - start);
                continue;
            }
            var afterComment = SkipCommentTo(sb, code, i);
            if (afterComment != i)
            {
                i = afterComment;
                continue;
            }

            if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith("new ", StringComparison.Ordinal))
            {
                var nameEnd = ScanIdentifierEnd(code, i + 4);
                if (nameEnd > i + 4 && nameEnd < code.Length && code[nameEnd] == '(')
                {
                    var name = code.Substring(i + 4, nameEnd - i - 4);
                    var mapped = mapException(name);
                    if (mapped != null)
                    {
                        sb.Append("new ").Append(mapped).Append('(');
                        i = nameEnd + 1;
                        continue;
                    }
                }
            }

            if (AtIdentifierBoundary(code, i) && code.AsSpan(i).StartsWith("instanceof ", StringComparison.Ordinal))
            {
                var nameEnd = ScanIdentifierEnd(code, i + 11);
                if (nameEnd > i + 11)
                {
                    var name = code.Substring(i + 11, nameEnd - i - 11);
                    var mapped = mapException(name);
                    if (mapped != null)
                    {
                        sb.Append("instanceof ").Append(mapped);
                        i = nameEnd;
                        continue;
                    }
                }
            }

            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 自 start 起的标识符结束下标（首个非标识符字符位置）；无标识符时返回 start。
    /// </summary>
    private static int ScanIdentifierEnd(string code, int start)
    {
        var i = start;
        while (i < code.Length && IsIdentifierChar(code[i]))
        {
            i++;
        }
        return i;
    }

    /// <summary>
    /// 判断 for 头内是否存在字符串/字符字面量之外的顶层 ";"。
    /// </summary>
    private static bool ContainsTopLevelSemicolon(string header)
    {
        for (var i = 0; i < header.Length; i++)
        {
            var ch = header[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(header, i) - 1;
                continue;
            }
            if (ch == ';')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 收集 for 头内字符串/字符字面量之外的顶层 ";" 下标。
    /// 注意：只按括号/花括号计深度，尖括号不算——条件里的比较符（i &lt; n）会误吞后续 ";"。
    /// </summary>
    private static List<int> FindTopLevelSemicolons(string header)
    {
        var result = new List<int>();
        var depth = 0;
        for (var i = 0; i < header.Length; i++)
        {
            var ch = header[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(header, i) - 1;
                continue;
            }

            switch (ch)
            {
                case '(':
                case '[':
                case '{':
                    depth++;
                    break;
                case ')':
                case ']':
                case '}':
                    depth--;
                    break;
                default:
                    if (ch == ';' && depth == 0)
                    {
                        result.Add(i);
                    }
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// 判断初始化器字符串中是否存在括号/尖括号外层的顶层逗号（多变量声明特征）。
    /// </summary>
    private static bool HasTopLevelComma(string s)
    {
        var depth = 0;
        foreach (var c in s)
        {
            switch (c)
            {
                case '(':
                case '[':
                case '{':
                case '<':
                    depth++;
                    break;
                case ')':
                case ']':
                case '}':
                case '>':
                    depth--;
                    break;
                default:
                    if (c == ',' && depth == 0)
                    {
                        return true;
                    }
                    break;
            }
        }
        return false;
    }

    private static bool AtIdentifierBoundary(string code, int index)
    {
        return index == 0 || !IsIdentifierChar(code[index - 1]);
    }

    /// <summary>
    /// 取声明串的末 token 作为变量名；空串/无 token 返回 null。
    /// </summary>
    private static string? ExtractVariableName(string left)
    {
        var tokens = left.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? null : tokens[^1];
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0) return false;
        var first = name[0];
        if (!(char.IsLetter(first) || first == '_' || first == '$')) return false;
        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$')) return false;
        }
        return true;
    }

    private static bool IsIdentifierChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '$';
    }

    /// <summary>
    /// 跳过自 start 起的字符串/字符字面量（含反斜杠转义），返回闭合引号后一位置；
    /// 未闭合时返回文本末尾。
    /// </summary>
    private static int SkipQuotedLiteral(string code, int start)
    {
        var quote = code[start];
        var i = start + 1;
        while (i < code.Length)
        {
            if (code[i] == '\\' && i + 1 < code.Length)
            {
                i += 2;
                continue;
            }
            if (code[i] == quote)
            {
                return i + 1;
            }
            i++;
        }
        return code.Length;
    }

    /// <summary>
    /// 自 openIndex 起的 "'" 找匹配的闭合 "'"（含反斜杠转义），返回下标；未闭合返回 -1。
    /// </summary>
    private static int FindClosingQuote(string code, int openIndex)
    {
        var i = openIndex + 1;
        while (i < code.Length)
        {
            if (code[i] == '\\' && i + 1 < code.Length)
            {
                i += 2;
                continue;
            }
            if (code[i] == '\'')
            {
                return i;
            }
            i++;
        }
        return -1;
    }

    /// <summary>
    /// 从 openIndex 起的 '(' 找匹配的 ')'；跳过字符串/字符字面量，返回下标；无匹配返回 -1。
    /// </summary>
    private static int FindMatchingParen(string code, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(code, i) - 1;
                continue;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
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
}
