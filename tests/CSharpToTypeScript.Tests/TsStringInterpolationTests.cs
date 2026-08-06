using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 18 字符串插值与 String.Format 测试（M2）：简单插值（无格式符）→ 模板字符串；
/// 带格式符插值与 string.Format → CsString.format（共享管线已把 C# {N} 占位符转成 Java
/// % 指令，发射前逆翻译回 C# 风格，spec §4.3.6 契约）；对象插值 String.valueOf(x) →
/// String(x)；4+ 段插值降级的 StringBuilder.append 链 → 模板字符串/拼接；raw 语句
/// （Console.WriteLine）中的 String.format 同步改写；P 格式符与字面 % 歧义 → 诊断。
/// 先写失败测试（红），实现后全绿；CsString.format 用例经 L3 tsc + compat 桩编译。
/// </summary>
public class TsStringInterpolationTests : TsConversionTestBase
{
    // ─── 简单插值 → 模板字符串 ────────────────────────────────────────

    [Fact]
    public void Simple_Interpolation_Emits_Template_String()
    {
        var result = Convert("""
class X
{
    string M(int n) { return $"count={n}"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return `count=${n}`;", result.GeneratedCode);
        Assert.DoesNotContain("\"count=\" + n", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Interpolation_Leading_Expression_Emits_Template_String()
    {
        // 链中含字符串字面量即保证整链为字符串拼接（C# 与 JS 规则一致），
        // 前导为表达式也安全重建：`${n}!`
        var result = Convert("""
class X
{
    string M(int n) { return $"{n}!"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return `${n}!`;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Interpolation_Multiple_Expressions_Emits_Template_String()
    {
        var result = Convert("""
class X
{
    string M(int a, int b) { return $"a{a} b{b} c"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return `a${a} b${b} c`;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Numeric_First_Chain_Not_Converted_To_Template()
    {
        // a + b + "x"：首个 + 可能为数值相加（C# 与 JS 求值一致），不重建为模板
        // （模板化会改变语义：数字分别字符串化 vs 先求和）；保持 + 链
        var result = Convert("""
class X
{
    string M(int a, int b) { return a + b + "x"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return a + b + \"x\";", result.GeneratedCode);
        Assert.DoesNotContain("`", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── 带格式符插值 → CsString.format ──────────────────────────────

    [Fact]
    public void Format_Spec_Interpolation_Emits_CsString_Format()
    {
        var result = Convert("""
class X
{
    string M(double d) { return $"{d:F2}"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0:F2}\", d);", result.GeneratedCode);
        Assert.Contains("import { CsString } from \"@csharptotypescript/compat\";", result.GeneratedCode);
        // CsString.format 内含 "String.format" 子串：仅断言原始 Java 调用形态不残留
        Assert.DoesNotContain(" String.format", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Format_Spec_With_Literal_Text_Emits_CsString_Format()
    {
        var result = Convert("""
class X
{
    string M(double d) { return $"Value: {d:F2}!"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"Value: {0:F2}!\", d);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Mixed_Plain_And_Format_Interpolation_Emits_CsString_Format()
    {
        var result = Convert("""
class X
{
    string M(int x, double d) { return $"{x} {d:F2}"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0} {1:F2}\", x, d);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    // ─── string.Format ───────────────────────────────────────────────

    [Fact]
    public void String_Format_Positional_Placeholders_Translated_Back()
    {
        var result = Convert("""
class X
{
    string M(int a, int b) { return string.Format("{0} {1}", a, b); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0} {1}\", a, b);", result.GeneratedCode);
        Assert.DoesNotContain("%1$s", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void String_Format_Repeated_Index_Preserved()
    {
        var result = Convert("""
class X
{
    string M(int x) { return string.Format("{0}/{0}", x); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0}/{0}\", x);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void String_Format_Format_Specifier_Translated_Back()
    {
        var result = Convert("""
class X
{
    string M(double d) { return string.Format("{0:F2}", d); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0:F2}\", d);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void String_Format_Literal_Braces_Escaped_Back()
    {
        var result = Convert("""
class X
{
    string M(int x) { return string.Format("{{{0}}}", x); }
}
""");
        Assert.True(result.Success);
        // 共享管线把 C# 字面花括号转义（{{/}}）原样留在 Java 串中（{{%1$s}}，Java 渲染
        // {{x}}），逆翻译按 Java 侧语义双写为 {{{{0}}}}（渲染 {{x}}）；管线缺陷记入已知限制
        Assert.Contains("return CsString.format(\"{{{{{0}}}}}\", x);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void String_Format_Hex_Specifier_Translated_Back()
    {
        var result = Convert("""
class X
{
    string M(int v) { return string.Format("{0:X}", v); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0:X}\", v);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void String_Format_Percent_Escape_Preserved()
    {
        var result = Convert("""
class X
{
    string M(int x) { return string.Format("50% {0}", x); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"50% {0}\", x);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void String_Format_IFormatProvider_Stripped()
    {
        var result = Convert("""
class X
{
    string M(int x) { return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", x); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0}\", x);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void String_Format_Non_Literal_Format_Uses_CsString()
    {
        // 非字面量格式串：共享管线走 StringHelper.formatCs（运行时转换 {N} 占位符），
        // TS 侧等价目标是 CsString.format（同样接受 C# 风格格式串）
        var result = Convert("""
class X
{
    string M(string fmt, int x) { return string.Format(fmt, x); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(fmt, x);", result.GeneratedCode);
        Assert.DoesNotContain("StringHelper", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void String_Format_Mixed_Specifiers_Translated_Back()
    {
        var result = Convert("""
class X
{
    string M(int a, double b, int c) { return string.Format("{0:D4} {1:N2} {2:x2}", a, b, c); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0:D4} {1:N2} {2:x2}\", a, b, c);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    // ─── 对象插值 String.valueOf → String(...) ───────────────────────

    [Fact]
    public void Object_Interpolation_Wraps_In_String()
    {
        var result = Convert("""
class X
{
    string M(object o) { return $"v={o}"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return `v=${String(o)}`;", result.GeneratedCode);
        Assert.DoesNotContain("String.valueOf", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Single_Object_Interpolation_Wraps_In_String()
    {
        var result = Convert("""
class X
{
    string M(object o) { return $"{o}"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return String(o);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Object_Pair_Interpolation_Concats_String_Calls()
    {
        // 链中无字符串字面量（两个表达式）→ 保守保持 + 拼接；String(o) + String(p)
        // 语义与 C# 插值一致（首个 + 两侧均为字符串）
        var result = Convert("""
class X
{
    string M(object o, object p) { return $"{o}{p}"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return String(o) + String(p);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── 4+ 段插值降级（StringBuilder.append 链）─────────────────────

    [Fact]
    public void Many_Part_Interpolation_Reconstructs_Template_String()
    {
        var result = Convert("""
class X
{
    string M(int a, int b, int c, int d) { return $"a{a}b{b}c{c}d{d}"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return `a${a}b${b}c${c}d${d}`;", result.GeneratedCode);
        Assert.DoesNotContain("StringBuilder", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── raw 语句（Console.WriteLine）────────────────────────────────

    [Fact]
    public void Console_WriteLine_With_String_Format_Uses_CsString()
    {
        var result = Convert("""
class X
{
    void M(int x) { System.Console.WriteLine(string.Format("{0}", x)); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("console.log(CsString.format(\"{0}\", x));", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Console_Interpolation_Keeps_Concatenation()
    {
        // raw 语句中的拼接链不做模板重建（文本扫描保守）：输出已是合法 TS，
        // 语义与 C# 插值一致（字符串字面量在链首）
        var result = Convert("""
class X
{
    void M(int n) { System.Console.WriteLine($"x={n}"); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("console.log(\"x=\" + n);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── 已知限制：P 格式符歧义 → 诊断 ───────────────────────────────

    [Fact]
    public void Percent_Specifier_Ambiguity_Emits_Diagnostic()
    {
        // 共享管线把 C# {0:P2}（×100 + %）与 {0:F2}%（字面 %）都转成 %.2f%%，
        // 逆翻译无法区分：按 Java 侧语义发射 {0:F2}% 并诊断
        var result = Convert("""
class X
{
    string M(double x) { return string.Format("{0:P2}", x); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0:F2}%\", x);", result.GeneratedCode);
        AssertDiagnostic(result, "percent");
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Unknown_Format_Specifier_Drops_Todo_Comment_With_Diagnostic()
    {
        // 共享管线对未知 C# 格式符（如 {0:Q}）产出 "%s /* TODO: format specifier Q */"：
        // 占位符按 %s 映射为 {0}，TODO 注释丢弃并诊断
        var result = Convert("""
class X
{
    string M(double d) { return $"{d:Q}"; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return CsString.format(\"{0} \", d);", result.GeneratedCode);
        AssertDiagnostic(result, "format specifier");
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    // ─── L2：Java→C# 格式串逆翻译（共享翻译器）────────────────────────

    [Fact]
    public void Translator_Positional_And_Precision()
    {
        var out1 = JavaToCSharpFormatTranslator.Translate("%1$s %2$.2f", out var c1, out _);
        Assert.Equal("{0} {1:F2}", out1);
        Assert.True(c1);

        var out2 = JavaToCSharpFormatTranslator.Translate("%1$04d %2$,.2f %3$2x", out var c2, out _);
        Assert.Equal("{0:D4} {1:N2} {2:x2}", out2);
        Assert.True(c2);
    }

    [Fact]
    public void Translator_Literal_Percent_And_Braces()
    {
        var out1 = JavaToCSharpFormatTranslator.Translate("50%% %1$s", out var c1, out _);
        Assert.Equal("50% {0}", out1);
        Assert.True(c1);

        // 共享管线缺陷：C# {{ 字面花括号转义未折叠（Java 串 {{%1$s}} 渲染 {{x}}），
        // 按 Java 侧语义逐字双写 → {{{{0}}}}；管线缺陷记入已知限制
        var out2 = JavaToCSharpFormatTranslator.Translate("{{%1$s}}", out var c2, out _);
        Assert.Equal("{{{{{0}}}}}", out2);
        Assert.True(c2);
    }

    [Fact]
    public void Translator_Unsupported_Directive_Kept_And_Flagged()
    {
        var out1 = JavaToCSharpFormatTranslator.Translate("%1$tY", out var c1, out _);
        Assert.Contains("%1$tY", out1);
        Assert.False(c1);
    }

    [Fact]
    public void Translator_Percent_After_Fixed_Point_Flagged_As_Ambiguous()
    {
        var translated = JavaToCSharpFormatTranslator.Translate("%.2f%%", out var complete, out var ambiguous);
        Assert.Equal("{0:F2}%", translated);
        Assert.True(complete);
        Assert.True(ambiguous);
    }
}
