using System.Globalization;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>字面量改写结果:新文本与数值(Roslyn 数字 token 工厂需要)。</summary>
internal readonly record struct UnsignedLiteralRewrite(string Text, ulong Value);

/// <summary>
/// 数值字面量的擦除安全化。C# 与 Java 整数字面量语法几乎一致,唯一无法机械
/// 翻译的是两种文本形态(依据 Java 语言规范,而非转换器实现):
/// <list type="number">
/// <item>十进制整数字面量值为 ulong 域且超出 long.MaxValue(含 u 系后缀,以及
/// Roslyn 同样按 ulong 接受的 L 后缀/无后缀高值):Java 十进制 long 不接受越界
/// 数字(Java 十六进制/二进制允许 0xFFFFFFFF / 0xFFFFFFFFFFFFFFFF,按补码取位
/// 模式)。改写为同后缀的十六进制,C# 类型与值均不变;一元负号下恰好 2^63 的
/// long.MinValue 特例在十六进制形态下两语言同样成立(-0x8000000000000000L)。
/// uint 域十进制超出 int.MaxValue 同理(擦除为 Java int)。</item>
/// <item>十进制整数带前导零(如 042):C# 无八进制,值为 42;Java 前导零是八进制,
/// 会静默变成 34。去除前导零,值与类型不变。</item>
/// </list>
/// 其余形态(十六进制/二进制任意后缀、十进制有符号范围、实数字面量、数字分隔符
/// _)在两种语言里语法与语义逐字兼容,保持原文以最小化 diff。
/// </summary>
internal static class UnsignedLiteralSyntax
{
    /// <summary>
    /// 给定数值字面量 token 的原始文本,计算擦除安全化后的文本;无需改写时返回
    /// false。只处理整数,实数/非数值文本返回 false。
    /// </summary>
    public static bool TryRewrite(string text, out UnsignedLiteralRewrite rewrite)
    {
        rewrite = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // 十六进制/二进制:Java 允许 int 到 0xFFFFFFFF、long 到 0xFFFF..FFFF,
        // 位模式恒可表示,任何后缀组合都不需要改写。
        if (text.Length >= 2 && text[0] == '0' && text[1] is 'x' or 'X' or 'b' or 'B')
        {
            return false;
        }

        // 尾部最多截取 2 个 u/U/l/L 后缀字符;其余必须是十进制数字(允许 _ 分隔符),
        // 否则是实数字面量(含 . e E f F d D m M)或非法文本,跳过。
        var suffixStart = text.Length;
        while (suffixStart > 0
               && text.Length - suffixStart < 2
               && text[suffixStart - 1] is 'u' or 'U' or 'l' or 'L')
        {
            suffixStart--;
        }

        var suffix = text[suffixStart..];
        var body = text[..suffixStart];
        if (body.Length == 0 || !char.IsAsciiDigit(body[0]))
        {
            return false;
        }

        foreach (var c in body)
        {
            if (!char.IsAsciiDigit(c) && c != '_')
            {
                return false;
            }
        }

        var digits = body.Replace("_", "");
        if (digits.Length == 0
            || !ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            // 超出 ulong.MaxValue 不是合法 C# 整数字面量,保持原文。
            return false;
        }

        // 去前导零(C# 十进制无八进制,Java 前导零按八进制解析)。
        var normalized = digits.TrimStart('0');
        if (normalized.Length == 0)
        {
            normalized = "0";
        }

        var hasUnsignedSuffix = suffix.Contains('u') || suffix.Contains('U');
        if (!hasUnsignedSuffix)
        {
            // Roslyn 对超过 long.MaxValue 的十进制 L 后缀/无后缀字面量同样按
            // ulong 接受(值域 2^63..2^64-1),一并按 ulong 域处理。
            if (value > long.MaxValue)
            {
                var highHex = "0x" + value.ToString("X", CultureInfo.InvariantCulture) + suffix;
                rewrite = new UnsignedLiteralRewrite(highHex, value);
                return true;
            }

            if (normalized != digits)
            {
                rewrite = new UnsignedLiteralRewrite(normalized + suffix, value);
            }

            return normalized != digits;
        }

        // 无符号类型判定按 C# 规则:u+l → ulong;仅 u 时值 ≤ uint.MaxValue 为
        // uint,否则升为 ulong。仅当十进制文本越出对应有符号宽度才转十六进制。
        var isUInt64 = suffix.Contains('l') || suffix.Contains('L') || value > uint.MaxValue;
        var outOfSignedRange = isUInt64 ? value > long.MaxValue : value > int.MaxValue;
        if (!outOfSignedRange)
        {
            if (normalized != digits)
            {
                rewrite = new UnsignedLiteralRewrite(normalized + suffix, value);
            }

            return normalized != digits;
        }

        var hex = "0x" + value.ToString("X", CultureInfo.InvariantCulture) + suffix;
        rewrite = new UnsignedLiteralRewrite(hex, value);
        return true;
    }

    public static bool NeedsRewrite(string text) => TryRewrite(text, out _);
}
