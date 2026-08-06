using System.Text;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 字符串调用改写 rewriter（Task 18）：共享管线把 C# 插值格式符/string.Format 降级为
/// Java String.format(...)（raw 文本）或 String.valueOf(...)（结构化/raw），TS 侧无这些 API：
/// - String.format("fmt", args...) → CsString.format("fmtC#", args...)：格式串逆翻译回
///   C# 风格 {N} 占位符（spec §4.3.6 CsString.format 契约）；compat 导入由发射器按
///   CompatSymbols 登记（rewriter 先于发射器运行且无共享实例）。
/// - StringHelper.formatCs(...) → CsString.format(...)：非字面量格式串（C# 侧运行时转换
///   {N} 占位符，与 CsString.format 语义一致）。
/// - String.valueOf(x) → String(x)（raw 文本形态；结构化形态由 TsExpressionWriter 处理）。
/// - new StringBuilder().append(...).toString()（4+ 段插值降级 / 内联 builder 链）→
///   模板字符串（链首为字面量）或 + 拼接。
/// 保守策略：仅改写可确认形态；格式串含未识别指令/歧义 → 诊断。raw 扫描跳过字符串/
/// 字符字面量与注释（与 TsEnumCallRewriter 同构）。
/// </summary>
public sealed class TsStringCallRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext? _context;
    private readonly HashSet<string> _compatSymbols = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emittedDiagnostics = new(StringComparer.Ordinal);

    public TsStringCallRewriter(ConversionContext? context = null)
    {
        _context = context;
    }

    /// <summary>发射期需从 @csharptotypescript/compat 导入的符号（如 CsString）。</summary>
    public IReadOnlySet<string> CompatSymbols => _compatSymbols;

    // ─── 遍历覆盖（与 TsEnumCallRewriter 同构：结构化 + raw 文本全覆盖） ───

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        node = base.VisitMethodDeclaration(node);
        if (!string.IsNullOrEmpty(node.Body))
        {
            node.Body = RewriteStringCalls(node.Body);
        }
        return node;
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        node = base.VisitConstructorDeclaration(node);
        if (!string.IsNullOrEmpty(node.Body))
        {
            node.Body = RewriteStringCalls(node.Body);
        }
        return node;
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var result = base.VisitClassDeclaration(node);
        // JavaSyntaxRewriter 不遍历静态初始化块（TsTypeWriter 直接发射 Statements），
        // 这里补改其中的字符串调用
        foreach (var block in node.StaticInitializers)
        {
            for (var i = 0; i < block.Statements.Count; i++)
            {
                block.Statements[i] = RewriteStringCalls(block.Statements[i]);
            }
        }
        return result;
    }

    public override JavaFieldDeclaration VisitFieldDeclaration(JavaFieldDeclaration node)
    {
        if (!string.IsNullOrEmpty(node.Initializer))
        {
            node.Initializer = RewriteStringCalls(node.Initializer);
        }
        return base.VisitFieldDeclaration(node);
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        if (!string.IsNullOrEmpty(node.Code))
        {
            node.Code = RewriteStringCalls(node.Code);
        }
        return base.VisitRawStatement(node);
    }

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        if (node is JavaRawExpression raw)
        {
            raw.Code = RewriteStringCalls(raw.Code);
            return raw;
        }
        return base.VisitExpression(node);
    }

    // ─── 文本改写主入口 ───────────────────────────────────────────────

    private string RewriteStringCalls(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 4+ 段插值降级 / 内联 builder 链：整链重建（append 实参已递归改写，
        // 避免对重建后的模板文本二次扫描误改字面内容）
        if (TryRewriteStringBuilderChain(text, out var rebuilt))
        {
            return rebuilt;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(text, i);
                sb.Append(text, start, i - start);
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                var start = i;
                i = SkipComment(text, i);
                sb.Append(text, start, i - start);
                continue;
            }
            if (IsIdentifierStart(ch) && AtIdentifierBoundaryBefore(text, i))
            {
                var nameEnd = i + 1;
                while (nameEnd < text.Length && IsIdentifierChar(text[nameEnd])) nameEnd++;
                var name = text[i..nameEnd];
                if (name == "String" && nameEnd < text.Length && text[nameEnd] == '.')
                {
                    var methodEnd = nameEnd + 1;
                    while (methodEnd < text.Length && IsIdentifierChar(text[methodEnd])) methodEnd++;
                    var method = text[(nameEnd + 1)..methodEnd];
                    if ((method == "format" || method == "valueOf")
                        && methodEnd < text.Length && text[methodEnd] == '(')
                    {
                        var close = FindMatchingParen(text, methodEnd);
                        if (close >= 0)
                        {
                            if (method == "valueOf")
                            {
                                // String.valueOf(x) → String(x)：实参文本原样透传
                                sb.Append("String(");
                                sb.Append(text, methodEnd + 1, close - methodEnd - 1);
                                sb.Append(')');
                                i = close + 1;
                                continue;
                            }
                            // String.format：首个实参为字符串字面量 → 逆翻译格式串
                            var firstArg = methodEnd + 1;
                            while (firstArg < close && char.IsWhiteSpace(text[firstArg])) firstArg++;
                            if (firstArg < close && text[firstArg] == '"')
                            {
                                var litEnd = SkipQuotedLiteral(text, firstArg);
                                var inner = text[(firstArg + 1)..(litEnd - 1)];
                                var translated = JavaToCSharpFormatTranslator.Translate(
                                    inner, out var complete, out var percentAmbiguous);
                                if (!complete)
                                {
                                    WarnOnce("String.format: some Java format directives (format specifiers) have no C# counterpart and were kept as-is (M4 compat stub may extend): output may differ from C#");
                                }
                                if (percentAmbiguous)
                                {
                                    WarnOnce("String.format: '%.Nf%%' is ambiguous (C# percent P specifier vs literal % after F); emitted as fixed-point + literal % (Java-flavor semantics, M4 may refine)");
                                }
                                _compatSymbols.Add("CsString");
                                sb.Append("CsString.format(");
                                sb.Append('"');
                                sb.Append(translated);
                                // 闭引号（litEnd - 1 处）+ 其余实参 + 调用闭括号原样保留
                                sb.Append(text, litEnd - 1, close - litEnd + 2);
                                i = close + 1;
                                continue;
                            }
                            // 首个实参非字面量：非 System.String 路径（自定义 String 类型），保守保留
                        }
                    }
                }
                if (name == "StringHelper" && nameEnd < text.Length && text[nameEnd] == '.')
                {
                    var methodEnd = nameEnd + 1;
                    while (methodEnd < text.Length && IsIdentifierChar(text[methodEnd])) methodEnd++;
                    var method = text[(nameEnd + 1)..methodEnd];
                    if (method == "formatCs" && methodEnd < text.Length && text[methodEnd] == '(')
                    {
                        var close = FindMatchingParen(text, methodEnd);
                        if (close >= 0)
                        {
                            // StringHelper.formatCs(fmt, ...) → CsString.format(fmt, ...)：
                            // 非字面量格式串由兼容库在运行时按 C# 风格 {N} 解析
                            _compatSymbols.Add("CsString");
                            sb.Append("CsString.format");
                            sb.Append(text, methodEnd, close - methodEnd + 1);
                            i = close + 1;
                            continue;
                        }
                    }
                }
            }
            sb.Append(ch);
            i++;
        }

        // 文本级字符串拼接链（方法体为 raw 文本时的插值降级产物）→ 模板字符串
        var rewritten = sb.ToString();
        return TryRewriteTextConcatChain(rewritten, out var templated) ? templated : rewritten;
    }

    /// <summary>
    /// 文本级字符串拼接链 → 模板字符串：仅处理整条语句即拼接链的形态（可选 `return `
    /// 前缀 + 可选 `;` 后缀），段内不得含顶层赋值/分号/块/三元/逗号且不以语句关键字开头
    /// （避免 if/赋值等被卷入 ${}）。段分割按顶层 +（括号/方括号/花括号深度，跳过
    /// 字符串/注释；++/+= 不算分割）。任一段为字符串字面量即整链为字符串拼接
    /// （C# 与 JS 规则一致），重建语义等价；纯表达式链（$"{n}{m}" 降级形态）保守保持。
    /// </summary>
    private static bool TryRewriteTextConcatChain(string text, out string result)
    {
        result = "";
        var prefix = "";
        var body = text.Trim();
        if (body.StartsWith("return ", StringComparison.Ordinal))
        {
            prefix = "return ";
            body = body["return ".Length..];
        }
        var suffix = "";
        if (body.EndsWith(";", StringComparison.Ordinal))
        {
            suffix = ";";
            body = body[..^1];
        }
        body = body.Trim();
        if (body.Length == 0) return false;

        var segments = SplitTopLevelConcat(body);
        if (segments.Count < 2) return false;

        // 仅链首或第二段为字符串字面量时才整链重建（首个 + 已确定为字符串拼接）；
        // x + y + "a" 形态首个 + 可能为数值相加（C#/JS 均如此），不重建
        var trimmed0 = segments[0].Trim();
        var trimmed1 = segments.Count > 1 ? segments[1].Trim() : "";
        var firstIsLiteral = IsSimpleStringLiteral(trimmed0);
        var secondIsLiteral = segments.Count > 1 && IsSimpleStringLiteral(trimmed1);
        if (!firstIsLiteral && !secondIsLiteral) return false;
        var anyExpression = false;
        foreach (var segment in segments)
        {
            var seg = segment.Trim();
            if (seg.Length == 0) return false;
            if (!IsSimpleStringLiteral(seg))
            {
                anyExpression = true;
                if (!IsChainExpression(seg)) return false;
            }
        }
        if (!anyExpression) return false;

        var sb = new StringBuilder();
        sb.Append('`');
        foreach (var segment in segments)
        {
            var seg = segment.Trim();
            if (IsSimpleStringLiteral(seg))
            {
                AppendTemplateText(sb, seg[1..^1]);
            }
            else
            {
                sb.Append("${").Append(seg).Append('}');
            }
        }
        sb.Append('`');
        result = prefix + sb + suffix;
        return true;
    }

    /// <summary>顶层 + 分割（跳过字符串/字符字面量、注释；++/+= 不算分割）。</summary>
    private static List<string> SplitTopLevelConcat(string text)
    {
        var segments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                i = SkipComment(text, i) - 1;
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
                    if (depth > 0) depth--;
                    break;
                case '+':
                    if (depth == 0 && (i + 1 >= text.Length || (text[i + 1] != '+' && text[i + 1] != '=')))
                    {
                        segments.Add(text[start..i]);
                        start = i + 1;
                    }
                    break;
            }
        }
        segments.Add(text[start..]);
        return segments;
    }

    /// <summary>链段必须是纯表达式：顶层不得含 =/;/:/,，不得含块花括号，不以语句关键字开头。</summary>
    private static bool IsChainExpression(string segment)
    {
        var depth = 0;
        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(segment, i) - 1;
                continue;
            }
            if (ch == '/' && i + 1 < segment.Length && (segment[i + 1] == '/' || segment[i + 1] == '*'))
            {
                i = SkipComment(segment, i) - 1;
                continue;
            }
            switch (ch)
            {
                case '(':
                case '[':
                    depth++;
                    break;
                case ')':
                case ']':
                    if (depth > 0) depth--;
                    break;
                case '{':
                case '}':
                    return false;
                case '=':
                case ';':
                case ':':
                case ',':
                    if (depth == 0) return false;
                    break;
            }
        }
        if (StartsWithStatementKeyword(segment)) return false;
        return true;
    }

    private static readonly HashSet<string> StatementKeywords = new(StringComparer.Ordinal)
    {
        "if", "while", "for", "do", "switch", "return", "throw", "try", "catch",
        "finally", "else", "case", "default", "using", "lock", "goto",
    };

    private static bool StartsWithStatementKeyword(string segment)
    {
        var trimmed = segment.TrimStart();
        if (trimmed.Length == 0 || !IsIdentifierStart(trimmed[0])) return false;
        var end = 1;
        while (end < trimmed.Length && IsIdentifierChar(trimmed[end])) end++;
        return StatementKeywords.Contains(trimmed[..end]);
    }

    /// <summary>
    /// new StringBuilder().append(...)...toString() 整链 → 模板字符串/拼接。
    /// 允许外层 return/分号包裹（方法体 raw 文本形态）；每个 append 实参先递归改写
    /// （实参可能含 String.valueOf/String.format），再重建，避免对模板文本二次扫描。
    /// </summary>
    private bool TryRewriteStringBuilderChain(string text, out string result)
    {
        result = "";
        var prefix = "";
        var body = text.Trim();
        if (body.StartsWith("return ", StringComparison.Ordinal))
        {
            prefix = "return ";
            body = body["return ".Length..];
        }
        var suffix = "";
        if (body.EndsWith(";", StringComparison.Ordinal))
        {
            suffix = ";";
            body = body[..^1];
        }
        body = body.Trim();

        const string builder = "new StringBuilder()";
        if (body.Length < builder.Length || !body.StartsWith(builder, StringComparison.Ordinal)) return false;

        var i = builder.Length;
        var parts = new List<string>();
        const string append = ".append(";
        while (i < body.Length)
        {
            if (i + append.Length <= body.Length
                && string.CompareOrdinal(body, i, append, 0, append.Length) == 0)
            {
                var openParen = i + append.Length - 1;
                var close = FindMatchingParen(body, openParen);
                if (close < 0) return false;
                parts.Add(RewriteStringCalls(body[(openParen + 1)..close].Trim()));
                i = close + 1;
                continue;
            }
            break;
        }

        const string toStringSuffix = ".toString()";
        if (body.Length - i != toStringSuffix.Length
            || string.CompareOrdinal(body, i, toStringSuffix, 0, toStringSuffix.Length) != 0)
        {
            // 近似但非精确形态的 builder 链（带构造实参等）：输出将无法编译，诊断
            WarnOnce("StringBuilder chains are not supported by the TypeScript target yet (M4 CsStringBuilder): output may not compile");
            return false;
        }

        if (parts.Count == 0)
        {
            result = prefix + "\"\"" + suffix;
            return true;
        }
        result = prefix + BuildConcat(parts) + suffix;
        return true;
    }

    /// <summary>
    /// 拼接段 → 模板字符串（任一段为字符串字面量即整链为字符串拼接，C# 与 JS 规则一致）；
    /// 纯表达式段（如 $"{o}{p}" 降级的 String(o) + String(p)）→ + 拼接。
    /// </summary>
    private static string BuildConcat(List<string> parts)
    {
        // 与文本/结构化链同规则：仅链首或第二段为字符串字面量时整链重建
        var firstIsLiteral = parts.Count > 0 && IsSimpleStringLiteral(parts[0]);
        var secondIsLiteral = parts.Count > 1 && IsSimpleStringLiteral(parts[1]);
        if (!firstIsLiteral && !secondIsLiteral)
        {
            return string.Join(" + ", parts);
        }
        var sb = new StringBuilder();
        sb.Append('`');
        foreach (var part in parts)
        {
            if (IsSimpleStringLiteral(part))
            {
                AppendTemplateText(sb, part[1..^1]);
            }
            else
            {
                sb.Append("${").Append(part).Append('}');
            }
        }
        sb.Append('`');
        return sb.ToString();
    }

    private static bool IsSimpleStringLiteral(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return false;
        for (var i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == '\\')
            {
                i++;
                continue;
            }
            if (s[i] == '"') return false;
        }
        return true;
    }

    /// <summary>模板文本段转义：反引号与 ${ 需转义；既有转义序列（\\、\" 等）语义不变。</summary>
    private static void AppendTemplateText(StringBuilder sb, string inner)
    {
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch == '`')
            {
                sb.Append("\\`");
                continue;
            }
            if (ch == '$' && i + 1 < inner.Length && inner[i + 1] == '{')
            {
                sb.Append("\\$");
                continue;
            }
            sb.Append(ch);
        }
    }

    private void WarnOnce(string message)
    {
        if (_context == null || !_emittedDiagnostics.Add(message)) return;
        _context.Diagnostics.Warning(message);
    }

    // ─── 文本扫描辅助（与 TsEnumCallRewriter 同构） ───────────────────

    private static bool IsIdentifierStart(char ch)
        => ch == '_' || char.IsLetter(ch);

    private static bool IsIdentifierChar(char ch)
        => ch == '_' || char.IsLetterOrDigit(ch);

    private static bool AtIdentifierBoundaryBefore(string text, int index)
    {
        if (index == 0) return true;
        var prev = text[index - 1];
        return !IsIdentifierChar(prev);
    }

    private static int SkipQuotedLiteral(string text, int start)
    {
        var quote = text[start];
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }
            if (text[i] == quote) return i + 1;
            i++;
        }
        return text.Length;
    }

    private static int SkipComment(string text, int start)
    {
        if (start + 1 < text.Length && text[start + 1] == '/')
        {
            var end = text.IndexOf('\n', start + 2);
            return end < 0 ? text.Length : end + 1;
        }
        var close = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
        return close < 0 ? text.Length : close + 2;
    }

    private static int FindMatchingParen(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            if (ch == '(') depth++;
            else if (ch == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}
