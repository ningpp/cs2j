using System.Globalization;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>字面量改写结果:新文本与数值(Roslyn 数字 token 工厂需要)。</summary>
internal readonly record struct UnsignedLiteralRewrite(string Text, ulong Value);

/// <summary>
/// 数值字面量的擦除安全化:产出"逐字机械翻译到 Java 即正确"的中间 C# 文本。
/// 判定只依据 C# 与 Java 语言规范的字面量文法,不依赖转换器实现。C# 字面量
/// 类型只由"后缀+值"决定、与上下文无关,因此下列改写全部保持类型与值不变
/// (十六进制+同后缀按 C# 类型规则得到相同类型;补 L 不改变 long/ulong 定型;
/// 一元负号下恰好 2^63 的 long.MinValue 特例在十六进制形态下两语言同样成立)。
/// <list type="number">
/// <item>十六进制/二进制前缀后的 `_`(如 0x_1F、0b_1010,Roslyn 接受):Java
/// 禁止下划线紧贴进制前缀,剥离之。</item>
/// <item>十进制整数,C# 类型为 uint 且值 &gt; int.MaxValue(擦除为 Java int),
/// 或类型为 ulong 且值 &gt; long.MaxValue(擦除为 Java long):Java 十进制不接受
/// 越界数字,转十六进制(Java 十六进制/二进制允许 0xFFFFFFFF /
/// 0xFFFFFFFFFFFFFFFF,按补码取位模式)。</item>
/// <item>C# 类型为 long/ulong 而后缀缺 L(十进制无后缀大值、仅 u 后缀的
/// ulong、十六进制/二进制 ≥ 2^32 无后缀等):Java long 字面量必须带 L 后缀,
/// 补 L。</item>
/// <item>十进制整数前导零(如 042):C# 无八进制,Java 前导零按八进制解析,
/// 去除前导零。</item>
/// </list>
/// 其余形态(十六进制/二进制 uint 域任意后缀、十进制有符号范围、实数字面量、
/// 数字之间的 `_`)在两种语言里语法与语义逐字兼容,保持原文以最小化 diff。
/// byte[] 初始化器元素 ≥ 128 的 (byte) 收窄需要语义上下文,见语法重写器。
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

        // 进制前缀(保留原大小写)与前缀后紧贴的 `_`(Java 禁止,如 0x_1F)。
        var isHexBinary = text.Length >= 2 && text[0] == '0' && text[1] is 'x' or 'X' or 'b' or 'B';
        var prefix = isHexBinary ? text[..2] : "";
        var rest = isHexBinary ? text[2..] : text;

        // 尾部最多截取 2 个 u/U/l/L 后缀字符;其余必须是数字(允许 `_` 分隔符),
        // 否则是实数字面量(含 . e E f F d D m M)或非法文本,跳过。
        var suffixStart = rest.Length;
        while (suffixStart > 0
               && rest.Length - suffixStart < 2
               && rest[suffixStart - 1] is 'u' or 'U' or 'l' or 'L')
        {
            suffixStart--;
        }

        var suffix = rest[suffixStart..];
        var digitsWithSeparators = rest[..suffixStart];
        if (digitsWithSeparators.Length == 0 || !IsValidFirstDigit(digitsWithSeparators[0], isHexBinary))
        {
            return false;
        }

        foreach (var c in digitsWithSeparators)
        {
            if (!char.IsAsciiDigit(c) && c != '_' && !IsBaseDigit(c))
            {
                return false;
            }
        }

        var digits = digitsWithSeparators.Replace("_", "");
        if (digits.Length == 0 || !TryParseValue(digits, isHexBinary, prefix, out var value))
        {
            // 超出 ulong.MaxValue 不是合法 C# 整数字面量,保持原文。
            return false;
        }

        var normalized = digits.TrimStart('0');
        if (normalized.Length == 0)
        {
            normalized = "0";
        }

        var hasU = suffix.Contains('u') || suffix.Contains('U');
        var hasL = suffix.Contains('l') || suffix.Contains('L');
        var literalType = GetLiteralType(value, isHexBinary, hasU, hasL);

        // 十六进制/二进制:保留原数字文本(仅当前缀后贴 `_` 时剥离),
        // 位模式在 Java 恒可表示;仅当 64 位类型缺 L 时补 L。
        // 十进制:越出擦除域的有符号宽度时转十六进制;前导零剥离。
        string numberText;
        var changed = false;
        if (isHexBinary)
        {
            var stripped = digitsWithSeparators.TrimStart('_');
            changed = !string.Equals(stripped, digitsWithSeparators, StringComparison.Ordinal);
            if (literalType is LiteralType.Long or LiteralType.ULong && !hasL)
            {
                numberText = prefix + stripped + suffix + "L";
                changed = true;
            }
            else
            {
                numberText = prefix + stripped + suffix;
            }

            if (!changed)
            {
                return false;
            }
        }
        else
        {
            var toHex = literalType == LiteralType.UInt && value > int.MaxValue
                        || literalType == LiteralType.ULong && value > long.MaxValue;
            var needL = literalType is LiteralType.Long or LiteralType.ULong && !hasL;
            changed = toHex || needL || normalized != digits;
            if (!changed)
            {
                return false;
            }

            numberText = toHex
                ? "0x" + value.ToString("X", CultureInfo.InvariantCulture) + suffix + (needL ? "L" : "")
                : normalized + suffix + (needL ? "L" : "");
        }

        rewrite = new UnsignedLiteralRewrite(numberText, value);
        return true;
    }

    public static bool NeedsRewrite(string text) => TryRewrite(text, out _);

    /// <summary>
    /// 首个数字字符:十进制只允许 0-9;十六进制/二进制允许十六进制数字,以及
    /// 前缀后紧贴的 `_`(0x_1F,Roslyn 接受、Java 禁止)。
    /// </summary>
    private static bool IsValidFirstDigit(char c, bool isHexBinary)
        => isHexBinary
            ? char.IsAsciiDigit(c) || IsBaseDigit(c) || c == '_'
            : char.IsAsciiDigit(c);

    /// <summary>十六进制数字(a-f/A-F);二进制越界数字由值解析兜底拒绝。</summary>
    private static bool IsBaseDigit(char c) => c is >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool TryParseValue(string digits, bool isHexBinary, string prefix, out ulong value)
    {
        if (!isHexBinary)
        {
            return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        var @base = prefix[1] is 'x' or 'X' ? 16 : 2;
        try
        {
            value = Convert.ToUInt64(digits, @base);
            return true;
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
        catch (OverflowException)
        {
            value = 0;
            return false;
        }
    }

    private enum LiteralType
    {
        Int,
        UInt,
        Long,
        ULong,
    }

    /// <summary>
    /// 按 C# 规则(含 Roslyn 对超过 long.MaxValue 的十进制 L/无后缀同样按
    /// ulong 接受的宽松行为)从后缀+值判定字面量类型。
    /// </summary>
    private static LiteralType GetLiteralType(ulong value, bool isHexBinary, bool hasU, bool hasL)
    {
        if (hasU && hasL)
        {
            return LiteralType.ULong;
        }

        if (hasU)
        {
            return value <= uint.MaxValue ? LiteralType.UInt : LiteralType.ULong;
        }

        if (value <= int.MaxValue)
        {
            return LiteralType.Int;
        }

        if (hasL)
        {
            return value <= long.MaxValue ? LiteralType.Long : LiteralType.ULong;
        }

        if (isHexBinary)
        {
            return value <= uint.MaxValue ? LiteralType.UInt
                : value <= long.MaxValue ? LiteralType.Long
                : LiteralType.ULong;
        }

        return value <= long.MaxValue ? LiteralType.Long : LiteralType.ULong;
    }
}
