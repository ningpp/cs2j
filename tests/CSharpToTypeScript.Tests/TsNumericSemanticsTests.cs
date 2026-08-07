namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 19 数值语义测试（M2）：类型感知的整数除法/窄化/混算（spec §5.3 M2 组）。
/// 整数除法（sbyte..uint 整型操作数）→ Math.trunc(l / r)；long/ulong（bigint）除法与
/// double 除法不误包；窄化转换 (byte)/(sbyte)/(short)/(ushort)/(uint)/(int)/(long) →
/// 位掩码/符号扩展/模 2^32 截断/BigInt/Number（bigint/object 源补 Number 包装）；
/// bigint/number 混算 + - * / % 与比较、移位、复合赋值 → 显式 BigInt(...)/Number(...)；
/// 字符串 + bigint → Number 包装；List.Clear() 语句 → x.length = 0；无法判定的
/// 类型参数与 bigint 混算 → 诊断。全部用例经 L3 tsc --noEmit 编译（Clear 用例用
/// global.d.ts 桩声明 CSharpList/Integer——List 泛型实参的 Java 风味名泄漏归既有
/// 基线 gap（Task 27/M4 类型映射），非本任务范围）。
/// </summary>
public class TsNumericSemanticsTests : TsConversionTestBase
{
    // ─── 整数除法 → Math.trunc ────────────────────────────────────────

    [Fact]
    public void Integer_Division_Wraps_Math_Trunc()
    {
        var result = Convert("""
class X
{
    int Div(int a, int b) { return a / b; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("Math.trunc(a / b)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Integer_Division_Literals_Wraps_Math_Trunc()
    {
        var result = Convert("""
class X
{
    int Div() { return 7 / 2; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("Math.trunc(7 / 2)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void UInt_Division_Wraps_Math_Trunc()
    {
        var result = Convert("""
class X
{
    uint Div(uint a, uint b) { return a / b; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("Math.trunc(a / b)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Double_Division_Not_Wrapped()
    {
        // 反向用例：double 除法必须原样保留（Math.trunc 会破坏小数语义）
        var result = Convert("""
class X
{
    double Div(double a, double b) { return a / b; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return a / b;", result.GeneratedCode);
        Assert.DoesNotContain("Math.trunc", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Long_Division_Not_Wrapped()
    {
        // bigint 除法 JS 天然截断，不包 Math.trunc（且 Math.trunc 不接受 bigint）
        var result = Convert("""
class X
{
    long Div(long a, long b) { return a / b; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return a / b;", result.GeneratedCode);
        Assert.DoesNotContain("Math.trunc", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── 窄化转换 ─────────────────────────────────────────────────────

    [Fact]
    public void Narrow_Casts_Use_Mask_And_SignExtend()
    {
        var result = Convert("""
class X
{
    byte CastByte(long x) { return (byte)x; }
    sbyte CastSByte(int x) { return (sbyte)x; }
    short CastShort(int x) { return (short)x; }
    ushort CastUShort(int x) { return (ushort)x; }
    uint CastUInt(double x) { return (uint)x; }
    int CastInt(double d) { return (int)d; }
    long CastLong(int x) { return (long)x; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return (Number(x) & 0xFF);", result.GeneratedCode);   // (byte)long → 掩码 + Number 包装
        Assert.Contains("return x << 24 >> 24;", result.GeneratedCode);        // (sbyte) → 符号扩展
        Assert.Contains("return x << 16 >> 16;", result.GeneratedCode);        // (short) → 符号扩展
        Assert.Contains("return x & 0xFFFF;", result.GeneratedCode);           // (ushort) → 掩码
        Assert.Contains("(Math.trunc(Number(x)) % 0x100000000 + 0x100000000) % 0x100000000", result.GeneratedCode); // (uint) → 模 2^32
        Assert.Contains("return Math.trunc(d);", result.GeneratedCode);        // (int)double → 截断
        Assert.Contains("return BigInt(x);", result.GeneratedCode);            // (long)int → bigint
        Assert.DoesNotContain("(int)", result.GeneratedCode);
        Assert.DoesNotContain("L;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Narrow_Cast_From_Double_And_Object_Sources()
    {
        var result = Convert("""
class X
{
    byte ByteDouble(double x) { return (byte)x; }
    int ByteObj(object o) { return (byte)o; }
    sbyte SByteObj(object o) { return (sbyte)o; }
    int UShortObj(object o) { return (ushort)o; }
    long CastLongD(double x) { return (long)x; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return (x & 0xFF);", result.GeneratedCode);           // (byte)double 原样掩码
        Assert.Contains("return (Number(o) & 0xFF);", result.GeneratedCode);   // (byte)object → Number 包装
        Assert.Contains("return Number(o) << 24 >> 24;", result.GeneratedCode);
        Assert.Contains("return Number(o) & 0xFFFF;", result.GeneratedCode);
        Assert.Contains("return BigInt(Math.trunc(x));", result.GeneratedCode);
        Assert.DoesNotContain("(int)", result.GeneratedCode);
        Assert.DoesNotContain("intValue", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Implicit_Numeric_Conversions_Do_Not_Leak_Java_Casts()
    {
        // 共享适配层在 TS 目标直接透传：int→long 提升与 byte→int 等隐式转换
        // 不再泄漏 Java 风味 (long)/(int) cast（非法 TS）
        var result = Convert("""
class X
{
    long Widen(int x) { return x; }
    double WidenD(int x) { return x; }
    int NarrowByte(byte b) { return b; }
    long WidenByte(byte b) { return b; }
}
""");
        Assert.True(result.Success);
        Assert.DoesNotContain("(long)", result.GeneratedCode);
        Assert.DoesNotContain("(int)", result.GeneratedCode);
        Assert.DoesNotContain("(double)", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── bigint/number 混算 ───────────────────────────────────────────

    [Fact]
    public void Mixed_BigInt_Number_Arithmetic_Inserts_Explicit_Conversions()
    {
        var result = Convert("""
class X
{
    long MixedLong(long a, int b) { return a + b; }
    long MixedSub(long a, int b) { return a - b; }
    long MixedMul(long a, int b) { return a * b; }
    long MixedDiv(long a, int b) { return a / b; }
    double MixedDouble(long a, double b) { return a + b; }
    bool MixedCmp(long a, int b) { return a > b; }
    string StrPlusLong(string s, long a) { return s + a; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return a + BigInt(b);", result.GeneratedCode);
        Assert.Contains("return a - BigInt(b);", result.GeneratedCode);
        Assert.Contains("return a * BigInt(b);", result.GeneratedCode);
        Assert.Contains("return a / BigInt(b);", result.GeneratedCode);
        Assert.Contains("return Number(a) + b;", result.GeneratedCode);
        Assert.Contains("return a > BigInt(b);", result.GeneratedCode);
        Assert.Contains("return s + Number(a);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Mixed_Shift_And_Compound_Assignment()
    {
        var result = Convert("""
class X
{
    long ShiftL(long x) { return x << 24; }
    long MixedShift(long x, int n) { return x << n; }
    long CompAdd(long a) { a += 2; return a; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return x << BigInt(24);", result.GeneratedCode);
        Assert.Contains("return x << BigInt(n);", result.GeneratedCode);
        Assert.Contains("a += BigInt(2);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Unresolved_Mixed_Operand_Emits_Diagnostic()
    {
        // 类型参数与已确认 bigint 混算：无法判定 → 诊断
        var result = Convert("""
class X
{
    long M<T>(long a, T b) { return a + b; }
}
""");
        Assert.True(result.Success);
        AssertDiagnostic(result, "unresolved operand type");
    }

    // ─── List.Clear() → length = 0 ────────────────────────────────────

    [Fact]
    public void List_Clear_Becomes_Length_Assignment()
    {
        var result = Convert("""
class X
{
    void ClearList(List<int> l) { l.Clear(); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("l.length = 0;", result.GeneratedCode);
        Assert.DoesNotContain("l.clear()", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode,
            "global.d.ts", "type Integer = number;\ntype CSharpList<T> = Array<T>;\n");
    }

    [Fact]
    public void List_Field_Clear_Becomes_Length_Assignment()
    {
        // 字段接收者（this.items.Clear()）同样改写；字段含 | null 联合不参与 tsc
        //（可空统一归 Task 20），此处仅断言改写形态
        var result = Convert("""
class X
{
    List<string> items;
    void ClearThis() { this.items.Clear(); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("this.items.length = 0;", result.GeneratedCode);
    }

    // --- const 初始化（注记7：Java 风味字面量归一化） -------------------

    [Fact]
    public void Const_UInt_MaxValue_Initializer_Normalizes_To_BigInt_Literal()
    {
        // const long = uint.MaxValue 经共享适配层包成 BigInt(4294967295L)（TS 分支），
        // TsMemberWriter.NormalizeLiteral 须归一化为合法 TS bigint 字面量 4294967295n
        var result = Convert("""
class X
{
    const long MaxUInt = uint.MaxValue;
    const long Big = 4294967295L;
}
""");
        Assert.True(result.Success);
        Assert.Contains("= 4294967295n;", result.GeneratedCode);
        Assert.DoesNotContain("4294967295L", result.GeneratedCode);
        Assert.DoesNotContain("BigInt(", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
}

