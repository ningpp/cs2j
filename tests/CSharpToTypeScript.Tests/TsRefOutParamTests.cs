using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：ref/out 参数发射（spec §4.3.1/§5.3）。IR 中 Java 目标用
/// io.github.ningpp.compat.Holder 包装（参数类型 + 调用点临时声明 + 读回），TS 侧
/// 统一为兼容库 `Ref&lt;T&gt;`（{ value: T }）+ `refOf(value)` / `outRef&lt;T&gt;()`：
/// 参数类型映射在 TsMemberMappingTable.Holder 分支，调用点 raw 文本由
/// TsRefOutRewriter 归一化。全部用例经 L3 tsc strict 编译（compat 桩含 Ref/refOf/outRef）。
/// </summary>
public class TsRefOutParamTests : TsConversionTestBase
{
    [Fact]
    public void Out_And_Ref_Params_Map_To_Ref_And_CallSites()
    {
        var result = Convert("""
public class Use
{
    public void M(out int x, ref int y) { x = 1; y = 2; }
    public int Call()
    {
        int a = 0; int b = 1;
        M(out a, ref b);
        return a + b;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public m(x: Ref<number>, y: Ref<number>): void", result.GeneratedCode);
        Assert.Contains("const _aHolder1 = refOf(a);", result.GeneratedCode);
        Assert.Contains("const _bRef = refOf(b);", result.GeneratedCode);
        Assert.Contains("a = _aHolder1.value;", result.GeneratedCode);
        Assert.Contains("import { Ref, refOf } from \"@csharptotypescript/compat\";", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Out_Var_Emits_OutRef_And_Typed_ReadBack()
    {
        var result = Convert("""
public class Use
{
    public void M(out int x) { x = 1; }
    public int Call()
    {
        M(out int r);
        return r;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("const _rHolder1 = outRef<number>();", result.GeneratedCode);
        Assert.Contains("let r: number = _rHolder1.value;", result.GeneratedCode);
        Assert.DoesNotContain("IntHolder", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Ref_On_String_And_User_Types()
    {
        var result = Convert("""
public class Box { public int V; }
public class Use
{
    public void M(ref string s, ref Box b) { s = "x"; b.V = 1; }
    public void Call()
    {
        string s = "";
        Box b = new Box();
        M(ref s, ref b);
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public m(s: Ref<string>, b: Ref<Box>): void", result.GeneratedCode);
        Assert.Contains("const _sRef = refOf(s);", result.GeneratedCode);
        Assert.Contains("const _bRef = refOf(b);", result.GeneratedCode);
        Assert.Contains("s = _sRef.value;", result.GeneratedCode);
        Assert.Contains("b = _bRef.value;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Ref_Forwarding_Passes_Holder_Directly()
    {
        var result = Convert("""
public class Use
{
    public void A(ref int x) { x = 1; }
    public void B(ref int y) { A(ref y); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public a(x: Ref<number>): void", result.GeneratedCode);
        Assert.Contains("public b(y: Ref<number>): void", result.GeneratedCode);
        Assert.Contains("a(y);", result.GeneratedCode);
        Assert.DoesNotContain("refOf", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Ref_On_Long_Emits_Ref_Bigint()
    {
        var result = Convert("""
public class Use
{
    public void M(ref long x) { x = 1; }
    public void Call() { long a = 0; M(ref a); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public m(x: Ref<bigint>): void", result.GeneratedCode);
        Assert.Contains("const _aRef = refOf(a);", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}