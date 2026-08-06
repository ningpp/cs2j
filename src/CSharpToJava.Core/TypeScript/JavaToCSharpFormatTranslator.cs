using System.Text;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// Java String.format 格式串 → C# 风格格式串（Task 18）。共享管线把 C# 插值格式符
/// （{0:F2}）与 string.Format 占位符翻译成 Java % 指令（%s / %1$s / %.2f 等），
/// 本翻译器逆映射回 CsString.format 的 {N} / {N:spec} 契约（spec §4.3.6）。
/// 仅覆盖共享管线实际产出的指令子集（ConvertCSharpFormatToJava + RewriteStringFormatLiteral）；
/// 未识别指令原样保留并置 complete=false，调用方发诊断。
/// </summary>
public static class JavaToCSharpFormatTranslator
{
    /// <summary>
    /// 翻译 Java 格式串（字符串字面量引号内侧文本，转义序列原样保留）为 C# 风格。
    /// </summary>
    /// <param name="javaFormat">Java 格式串内文（不含外层引号）。</param>
    /// <param name="complete">全部指令均成功映射（否则含未识别指令或丢失的格式符注释）。</param>
    /// <param name="percentAmbiguous">出现 f 指令后紧跟 %% 的歧义形态：C# {N:P}（×100 + %）
    /// 与 {N:F} + 字面 % 在共享管线中同形（%.Nf%%），无法区分（按 Java 侧语义发射）。</param>
    public static string Translate(string javaFormat, out bool complete, out bool percentAmbiguous)
    {
        complete = true;
        percentAmbiguous = false;
        var sb = new StringBuilder(javaFormat.Length + 8);
        var argIndex = 0;
        var i = 0;
        while (i < javaFormat.Length)
        {
            var ch = javaFormat[i];
            if (ch == '%')
            {
                // %% → 字面 %
                if (i + 1 < javaFormat.Length && javaFormat[i + 1] == '%')
                {
                    sb.Append('%');
                    i += 2;
                    continue;
                }
                if (!TryParseDirective(javaFormat, i, out var directive, out var consumed))
                {
                    complete = false;
                    sb.Append(javaFormat[i]);
                    i++;
                    continue;
                }
                var idx = directive.PositionalIndex ?? argIndex;
                if (directive.PositionalIndex == null) argIndex++;
                if (TryMapDirective(directive, idx, out var mapped))
                {
                    sb.Append(mapped);
                }
                else
                {
                    complete = false;
                    sb.Append(javaFormat, i, consumed);
                }
                i += consumed;
                // %.Nf 后紧跟 %%：C# {N:P}（百分比，×100）与 {N:F}+字面 % 同形，无法区分；
                // 按 Java 侧语义发射（f + 字面 %），调用方发诊断
                if (char.ToLowerInvariant(directive.Conv) == 'f'
                    && i + 1 < javaFormat.Length
                    && javaFormat[i] == '%' && javaFormat[i + 1] == '%')
                {
                    percentAmbiguous = true;
                    sb.Append('%');
                    i += 2;
                }
                continue;
            }
            if (ch == '{' || ch == '}')
            {
                // C# 格式串中字面花括号需双写（{{ / }}）
                sb.Append(ch).Append(ch);
                i++;
                continue;
            }
            if (ch == '/' && i + 1 < javaFormat.Length && javaFormat[i + 1] == '*')
            {
                // 共享管线未知格式符占位注释（"%s /* TODO: format specifier Q */"）：
                // 占位符已按 %s 映射，注释仅标记丢失的原始格式符 → 丢弃 + 调用方诊断
                complete = false;
                var close = javaFormat.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? javaFormat.Length : close + 2;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryParseDirective(string s, int start, out Directive directive, out int consumed)
    {
        directive = default;
        consumed = 0;
        var i = start + 1;
        // 位置参数 %N$（N 为 1 基）；未跟 $ 的数字回退为宽度（%2x、%04d）
        int? positional = null;
        var digitsStart = i;
        while (i < s.Length && IsDigit(s[i])) i++;
        if (i > digitsStart && i < s.Length && s[i] == '$')
        {
            positional = int.Parse(s[digitsStart..i]) - 1;
            i++;
        }
        else
        {
            i = digitsStart;
        }
        // flags：0（零填充）、,（千分位）、-（左对齐）、+ / 空格
        var zero = false;
        var grouping = false;
        var minus = false;
        while (i < s.Length && (s[i] == '0' || s[i] == ',' || s[i] == '-' || s[i] == '+' || s[i] == ' '))
        {
            if (s[i] == '0') zero = true;
            else if (s[i] == ',') grouping = true;
            else if (s[i] == '-') minus = true;
            i++;
        }
        // width
        var widthStart = i;
        while (i < s.Length && IsDigit(s[i])) i++;
        var width = i > widthStart ? s[widthStart..i] : "";
        // precision
        var precision = "";
        if (i < s.Length && s[i] == '.')
        {
            i++;
            var precStart = i;
            while (i < s.Length && IsDigit(s[i])) i++;
            precision = i > precStart ? s[precStart..i] : "";
        }
        if (i >= s.Length || !char.IsLetter(s[i])) return false;
        consumed = i + 1 - start;
        directive = new Directive(positional, zero, grouping, minus, width, precision, s[i]);
        return true;
    }

    /// <summary>
    /// 指令 → C# 占位符。返回 false 表示无对应映射（调用方保留原文并诊断）。
    /// 映射表与共享管线 ConvertCSharpFormatToJava 的 D/F/E/X/N/G/P 子集互逆。
    /// </summary>
    private static bool TryMapDirective(Directive d, int idx, out string mapped)
    {
        mapped = "";
        var conv = char.ToLowerInvariant(d.Conv);
        switch (conv)
        {
            case 's':
                // %.3s 字符串截断无 C# 对应
                if (d.Precision.Length > 0) return false;
                mapped = $"{{{idx}}}";
                return true;
            case 'd':
                if (d.Grouping) return false;
                if (d.Zero)
                {
                    // %04d（C# {0:D4} 零填充）→ {idx:D4}；孤零标志无宽度时退化为 {idx}
                    mapped = d.Width.Length > 0 ? $"{{{idx}:D{d.Width}}}" : $"{{{idx}}}";
                    return true;
                }
                if (d.Width.Length > 0)
                {
                    // %4d 空格填充 → C# 对齐 {idx,4}（- 标志 → 左对齐 {idx,-4}）
                    mapped = d.Minus ? $"{{{idx},-{d.Width}}}" : $"{{{idx},{d.Width}}}";
                    return true;
                }
                mapped = $"{{{idx}}}";
                return true;
            case 'f':
                if (d.Zero || d.Width.Length > 0) return false;
                if (d.Grouping)
                {
                    // %,.2f（C# {0:N2} 千分位）→ {idx:N2}
                    mapped = d.Precision.Length > 0 ? $"{{{idx}:N{d.Precision}}}" : $"{{{idx}:N}}";
                    return true;
                }
                mapped = d.Precision.Length > 0 ? $"{{{idx}:F{d.Precision}}}" : $"{{{idx}:F}}";
                return true;
            case 'e':
                if (d.Zero || d.Width.Length > 0 || d.Grouping) return false;
                mapped = d.Precision.Length > 0 ? $"{{{idx}:E{d.Precision}}}" : $"{{{idx}:E}}";
                return true;
            case 'g':
                if (d.Zero || d.Width.Length > 0 || d.Grouping) return false;
                mapped = d.Precision.Length > 0 ? $"{{{idx}:G{d.Precision}}}" : $"{{{idx}:G}}";
                return true;
            case 'x':
                if (d.Grouping || d.Minus) return false;
                // C# {N:xW} 精度 = 零填充宽度；共享管线由 {0:xN} 生成 %Nx（空格填充语义
                // 丢失），逆映射还原 C# 零填充意图（Java 侧已按空格填充执行，记入已知限制）
                mapped = d.Width.Length > 0 ? $"{{{idx}:{d.Conv}{d.Width}}}" : $"{{{idx}:{d.Conv}}}";
                return true;
            default:
                // t（日期时间）、c、b、h、o、n 等共享管线不产出，保留原文 + 诊断
                return false;
        }
    }

    private static bool IsDigit(char ch) => ch >= '0' && ch <= '9';

    private readonly record struct Directive(
        int? PositionalIndex, bool Zero, bool Grouping, bool Minus, string Width, string Precision, char Conv);
}
