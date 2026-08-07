namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 20 可空值类型与 null 统一测试（M2，spec §5.3 M2 组）。
/// 类型映射：IR 整串装箱名（Integer/Long/Boolean/…）仅来自 C# 可空值类型，
/// 场/参/返回值 → `number | null`/…（泛型实参等非可空位置不宽化）；
/// `.HasValue` → `!== null`、`.Value` → 直接取值（非空断言）；
/// `??`/`??=` 直译为 TS 原生运算符；产物统一用 null 不出现 undefined。
/// 全部用例经 L3 tsc --noEmit 编译。
/// </summary>
public class TsNullableHandlingTests : TsConversionTestBase
{
    // ─── 类型映射：装箱名 → 可空联合 ────────────────────────────

    [Fact]
    public void Nullable_Fields_Map_To_Union_Null()
    {
        var result = Convert("""
class X
{
    int? a;
    int? b = null;
    int? c = 5;
    long? d = 7;
    bool? e = true;
    double? f = 1.5;
    char? g = 'x';
    object? o = null;
}
""");
        Assert.True(result.Success);
        Assert.Contains("a: number | null = null", result.GeneratedCode);
        Assert.Contains("b: number | null = null", result.GeneratedCode);
        Assert.Contains("c: number | null = 5", result.GeneratedCode);
        Assert.Contains("d: bigint | null = 7n", result.GeneratedCode);
        Assert.Contains("e: boolean | null = true", result.GeneratedCode);
        Assert.Contains("f: number | null = 1.5", result.GeneratedCode);
        Assert.Contains("g: string | null = \"x\"", result.GeneratedCode);
        Assert.Contains("o: any = null", result.GeneratedCode);   // any 吸收 null，不追加 | null
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Nullable_Method_Params_And_Returns_Map_To_Union_Null()
    {
        var result = Convert("""
class X
{
    int? M(int? p, long? q)
    {
        return p;
    }
    Nullable<int> N(Nullable<int> n)
    {
        return n;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("m(p: number | null, q: bigint | null): number | null", result.GeneratedCode);
        Assert.Contains("n(n: number | null): number | null", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Generic_Type_Arguments_Stay_Non_Nullable()
    {
        // 泛型实参（IR 中为 Java 装箱名）是非可空上下文：
        // implements IRepo<int> → IRepo<number>（不追加 | null）
        var result = Convert("""
interface IRepo<T>
{
    T Find();
}
class Foo : IRepo<int>
{
    public int Find() { return 0; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("implements IRepo<number>", result.GeneratedCode);
        Assert.DoesNotContain("IRepo<number | null>", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── .HasValue / .Value 展开 ────────────────────────────────

    [Fact]
    public void HasValue_Emits_Null_Check()
    {
        var result = Convert("""
class X
{
    int M(int? p)
    {
        if (p.HasValue) { return p.Value; }
        return 0;
    }
    bool Check(int? p) { return p.HasValue; }
    int Ternary(int? p) { return p.HasValue ? p.Value : 0; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("if ((p !== null))", result.GeneratedCode);
        Assert.Contains("return (p!);", result.GeneratedCode);
        Assert.Contains("return (p !== null);", result.GeneratedCode);
        Assert.DoesNotContain("intValue", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Value_Unwraps_With_NonNull_Assertion()
    {
        var result = Convert("""
class X
{
    int Add(int? p) { return p.Value + 1; }
    int? field;
    int FromField() { return this.field.Value; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return (p!) + 1;", result.GeneratedCode);
        Assert.Contains("return (this.field!);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── ?? / ??= 直译 ─────────────────────────────────

    [Fact]
    public void Coalesce_Emits_Native_Operator()
    {
        var result = Convert("""
class X
{
    int? Coalesce(int? x, int? y) { return x ?? y; }
    int? Chain(int? a, int? b, int? c) { return a ?? b ?? c; }
    int Mix(int? a) { return (a ?? 0) + 1; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return (x ?? y);", result.GeneratedCode);
        Assert.Contains("return (a ?? (b ?? c));", result.GeneratedCode);
        Assert.Contains("return ((a ?? 0)) + 1;", result.GeneratedCode);
        Assert.DoesNotContain("!= null ?", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Coalesce_Assignment_Emits_Native_Operator()
    {
        var result = Convert("""
class X
{
    void M(int? x) { x ??= 5; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("x ??= 5;", result.GeneratedCode);
        Assert.DoesNotContain("== null", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    // ─── null 统一性：不出现 undefined ─────────────────────

    [Fact]
    public void Null_Unification_No_Undefined()
    {
        var result = Convert("""
class X
{
    int? a = null;
    string? s = null;
    object? o = null;
    bool Check(int? p) { return p.HasValue; }
    int? Coalesce(int? x, int? y) { return x ?? y; }
    void Assign(int? x) { x ??= 5; }
}
""");
        Assert.True(result.Success);
        Assert.DoesNotContain("undefined", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
}
