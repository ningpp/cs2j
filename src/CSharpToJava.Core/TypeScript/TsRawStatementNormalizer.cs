namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 最小 raw 语句归一化（M1，Task 11 引入，计划外必需项）。
/// 前端对 Console.WriteLine/foreach 等不产出结构化节点，IR 中为 JavaRawStatement
/// 整段直通（如 "System.out.println(""hello"");"、"for (int item : arr) {...}"）。
/// 分叉后 TS 输出要合法且计划断言可转绿，发射前做最小文本归一化：
/// ① System.out.println → console.log、System.err.println → console.error（保留实参与分号）；
/// ② Java 增强 for 头 "for (Type var : expr)" → "for (const var of expr)"（Type 丢弃、
///    const 推断；expr 与 body 原样保留）。不匹配时原样直通。
/// 表达式层重写、IR 结构化、S 级 rewriter 均属 M2+，本类不做。
/// </summary>
public static class TsRawStatementNormalizer
{
    public static string Normalize(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return code;
        }

        // ① println/err.println → console.log/console.error。
        // 使用整串替换而非前缀匹配，WriteLineIf 产生的 "if (cond) System.out.println(x);"
        // 同样命中；实参与分号在替换串之外原样保留。
        var normalized = code
            .Replace("System.out.println(", "console.log(")
            .Replace("System.err.println(", "console.error(");

        // ② Java 增强 for 头归一化（不匹配则直通）。
        return NormalizeEnhancedForHead(normalized);
    }

    /// <summary>
    /// "for (Type var : expr) { ... }" → "for (const var of expr) { ... }"。
    /// 只处理以 "for (" 开头、头内含 " : "、且 " : " 左侧末 token 为标识符的 raw 语句；
    /// 普通 for 循环（无 " : "）与头内三元表达式（左侧末 token 非标识符）原样直通。
    /// </summary>
    private static string NormalizeEnhancedForHead(string code)
    {
        if (!code.StartsWith("for (", StringComparison.Ordinal))
        {
            return code;
        }

        var close = FindMatchingParen(code, 4);
        if (close < 0)
        {
            return code;
        }

        var header = code.Substring(5, close - 5);
        var sepIndex = header.IndexOf(" : ", StringComparison.Ordinal);
        if (sepIndex < 0)
        {
            return code;
        }

        // 左侧 "Type var"：Type 丢弃（const 推断），变量名取末 token（如 "Map.Entry<K, V> entry"）。
        var left = header.Substring(0, sepIndex).Trim();
        var right = header.Substring(sepIndex + 3).Trim();
        var varName = ExtractVariableName(left);
        if (varName == null || !IsIdentifier(varName))
        {
            return code;
        }

        // "{" 与后续 body 原样保留。
        return $"for (const {varName} of {right}){code.Substring(close + 1)}";
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
                var quote = ch;
                i++;
                while (i < code.Length)
                {
                    if (code[i] == '\\')
                    {
                        i++;
                    }
                    else if (code[i] == quote)
                    {
                        break;
                    }
                    i++;
                }
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
