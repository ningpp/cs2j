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
    public static string Normalize(string code, Func<string, string>? mapType = null, Func<string, string?>? mapException = null)
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
        var withChars = NormalizeCharLiterals(withFors);

        // ④ Java 风味异常名 → 兼容库名（Task 21：方法体直通文本中的 throw new X(...) /
        // instanceof X；结构化 new/instanceof/catch 由发射器在节点层映射）。
        var withExceptions = NormalizeExceptionNames(withChars, mapException);

        // ⑤ long 字面量 L 后缀 → n 后缀（Task 22 冒烟：raw 文本中 7L → 7n；数值语义
        // rewriter 改写出的 BigInt(...) 混合链同样落在 raw 文本，必须归一化）。
        return NormalizeLongLiterals(withExceptions);
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

        head = $"for (const {varName} of {right})";
        return true;
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

        var m = Regex.Match(initPart, @"^(?:final\s+)?(\w[\w.<>\[\], ]*?)\s+(\w+)\s*=\s*(.+)$");
        if (!m.Success)
        {
            return false;   // 无初始化等不匹配形态直通
        }

        var init = m.Groups[3].Value;

        // 多变量（如 "int i = 0, j = 1"）无法用单个 let 表达 → 直通
        if (HasTopLevelComma(init))
        {
            return false;
        }

        var javaType = m.Groups[1].Value.Trim();
        var name = m.Groups[2].Value;

        // 泛型/数组类型无法用简单类型注解表达 → 直通（M2 泛型任务细化）
        if (javaType.IndexOfAny(new[] { '<', '>', '[', ']' }) >= 0)
        {
            return false;
        }

        // IR 类型恒为 Java 风味，映射命中必改变文本；未命中（返回原值）→ 直通
        var tsType = mapType(javaType);
        if (tsType == javaType)
        {
            return false;
        }

        head = $"for (let {TsNaming.SafeIdentifier(name)}: {tsType} = {init}; {condPart}; {incPart})";
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
