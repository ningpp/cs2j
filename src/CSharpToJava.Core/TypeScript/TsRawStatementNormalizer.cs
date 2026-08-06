using System.Text;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 最小 raw 语句归一化（M1，Task 11 引入，计划外必需项）。
/// 前端对 Console.WriteLine/foreach 等不产出结构化节点，IR 中为 JavaRawStatement
/// 整段直通（如 "System.out.println(""hello"");"、"for (int item : arr) {...}"）。
/// 分叉后 TS 输出要合法且计划断言可转绿，发射前做最小文本归一化：
/// ① System.out.println → console.log、System.err.println → console.error（保留实参与分号）；
/// ② Java 增强 for 头 "for (Type var : expr)" → "for (const var of expr)"（Type 丢弃、
///    const 推断；expr 与 body 原样保留）。不匹配时原样直通。
/// Task 11 复审修复（2026-08-07）：
/// - 改写/替换均跳过字符串/字符字面量区域且只在标识符边界命中（实参含 "System.out.println("
///   文本的字符串字面量不被改写）；
/// - 增强 for 头增加顶层 ";" 守卫（普通 for 含任意三元，含标识符操作数，一律直通）；
/// - 同一 raw 文本内所有增强 for 头均归一化（嵌套 foreach 内外层都改写，body 原样保留）。
/// 表达式层重写、IR 结构化、S 级 rewriter 均属 M2+，本类不做。
/// </summary>
public static class TsRawStatementNormalizer
{
    private const string OutPrintln = "System.out.println(";
    private const string ErrPrintln = "System.err.println(";

    public static string Normalize(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return code;
        }

        // ① println/err.println → console.log/console.error。
        // 扫描替换：跳过字符串/字符字面量区域（实参含 "System.out.println(" 文本时不被改写），
        // 仅在标识符边界命中；WriteLineIf 产生的 "if (cond) System.out.println(x);" 同样命中。
        var normalized = NormalizePrintln(code);

        // ② Java 增强 for 头归一化（同一文本内所有头，含嵌套；不匹配则直通）。
        return NormalizeEnhancedForHeads(normalized);
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
    /// "for (Type var : expr) ..." → "for (const var of expr) ..."。
    /// 扫描同一文本内所有 "for (" 头（含嵌套 foreach 的 body 内层头），逐个改写；
    /// 其余文本原样保留。
    /// </summary>
    private static string NormalizeEnhancedForHeads(string code)
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
                if (close >= 0 && TryRewriteForHead(code, i, close, out var head))
                {
                    sb.Append(head);
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
    /// 尝试把 [openIndex, closeIndex] 范围内的 for 头改写为 for-of 头；不匹配返回 false。
    /// 头内 " : " 之前的部分必须形如 "Type var"（末 token 为标识符），且头内无顶层 ";"
    /// （增强 for 头不含分号；普通 for 含任意三元均直通）。
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
