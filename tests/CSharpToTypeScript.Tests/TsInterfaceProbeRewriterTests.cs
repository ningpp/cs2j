using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 25：接口运行时探针（spec §2.6.3）。每接口生成 is$IXxx 探针函数
/// （必需方法存在性检查 + 继承链合并）；is/as 调用点改写；
/// 实现类构造器注入 CsRuntime.markImplements 标记；纯属性接口诊断。
/// x is IFoo → is$IFoo(x)；x as IFoo → (is$IFoo(x) ? (x as IFoo) : null) as IFoo。
/// </summary>
public class TsInterfaceProbeRewriterTests : TsConversionTestBase
{
    [Fact]
    public void Is_Interface_Emits_Probe_Call_And_Probe_Function()
    {
        var result = Convert("""
interface IFoo
{
    void M();
}
class F
{
    bool A(object x) { return x is IFoo; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return is$IFoo(x);", result.GeneratedCode);
        Assert.Contains("export function is$IFoo(v: unknown): boolean", result.GeneratedCode);
        Assert.Contains("typeof (v as { m?: unknown }).m === \"function\"", result.GeneratedCode);
        Assert.Contains("CsRuntime.isMarked(v, \"IFoo\")", result.GeneratedCode);
        Assert.DoesNotContain("instanceof IFoo", result.GeneratedCode);
    }

    [Fact]
    public void As_Interface_Emits_Probe_Guarded_Cast()
    {
        var result = Convert("""
interface IFoo
{
    void M();
}
class F
{
    IFoo A(object x) { return x as IFoo; }
}
""");
        Assert.True(result.Success);
        // strict 下 : null 分支类型不符，整体再 as 回目标类型（C# as 运行时仍可返回 null）
        Assert.Contains("return (is$IFoo(x) ? (x as IFoo) : null) as IFoo;", result.GeneratedCode);
        Assert.DoesNotContain("instanceof IFoo", result.GeneratedCode);
        Assert.DoesNotContain("(IFoo)(x)", result.GeneratedCode);
    }

    [Fact]
    public void Probe_Merges_Inherited_Interface_Methods()
    {
        var result = Convert("""
interface IBase
{
    void B();
}
interface IFoo : IBase
{
    void M();
}
class F
{
    bool A(object x) { return x is IFoo; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("typeof (v as { b?: unknown }).b === \"function\"", result.GeneratedCode);
        Assert.Contains("typeof (v as { m?: unknown }).m === \"function\"", result.GeneratedCode);
    }

    [Fact]
    public void Implementing_Class_Constructor_Injects_Mark()
    {
        var result = Convert("""
interface IFoo
{
    void M();
}
class C : IFoo
{
    public C(int x) { }
    public void M() { }
}
""");
        Assert.True(result.Success);
        Assert.Contains("CsRuntime.markImplements(this, \"IFoo\");", result.GeneratedCode);
    }

    [Fact]
    public void Implementing_Class_Without_Constructor_Synthesizes_Mark_Constructor()
    {
        var result = Convert("""
interface IFoo
{
    void M();
}
class C : IFoo
{
    public void M() { }
}
""");
        Assert.True(result.Success);
        Assert.Contains("constructor() {", result.GeneratedCode);
        Assert.Contains("CsRuntime.markImplements(this, \"IFoo\");", result.GeneratedCode);
    }

    [Fact]
    public void Generic_Interface_Probe_And_Call_Sites()
    {
        var result = Convert("""
interface IRepo<T>
{
    T Get();
}
class C : IRepo<string>
{
    public string Get() { return "x"; }
}
class F
{
    bool A(object x) { return x is IRepo<string>; }
    IRepo<string> B(object x) { return x as IRepo<string>; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("export function is$IRepo(v: unknown): boolean", result.GeneratedCode);
        Assert.Contains("is$IRepo(x)", result.GeneratedCode);
        Assert.Contains("(x as IRepo<string>)", result.GeneratedCode);
        Assert.Contains("typeof (v as { get$?: unknown }).get$ === \"function\"", result.GeneratedCode);
    }

    [Fact]
    public void Pure_Property_Interface_Emits_Mark_Only_Probe_With_Diagnostic()
    {
        var result = Convert("""
interface IFoo
{
    int P { get; }
}
class F
{
    bool A(object x) { return x is IFoo; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("export function is$IFoo(v: unknown): boolean", result.GeneratedCode);
        Assert.Contains("return CsRuntime.isMarked(v, \"IFoo\");", result.GeneratedCode);
        Assert.DoesNotContain("typeof (v as IFoo)", result.GeneratedCode);
        AssertDiagnostic(result, "structural probe");
    }

    [Fact]
    public void Class_InstanceOf_Is_Not_Rewritten()
    {
        var result = Convert("""
class Widget { }
class F
{
    bool A(object x) { return x is Widget; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return x instanceof Widget;", result.GeneratedCode);
        Assert.DoesNotContain("is$Widget", result.GeneratedCode);
    }

    [Fact]
    public void Nested_Interface_Qualified_InstanceOf_Is_Rewritten()
    {
        var result = Convert("""
class Outer
{
    public interface IFoo
    {
        void M();
    }
}
class F
{
    bool A(object x) { return x is Outer.IFoo; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return is$IFoo(x);", result.GeneratedCode);
        Assert.Contains("export function is$IFoo(v: unknown): boolean", result.GeneratedCode);
        Assert.DoesNotContain("instanceof Outer.IFoo", result.GeneratedCode);
    }

    [Fact]
    public void Interface_Sample_Compiles_L3()
    {
        var result = Convert("""
interface IBase
{
    void B();
}
interface IFoo : IBase
{
    void M();
    int P { get; }
}
class C : IFoo
{
    public C() { }
    public void B() { }
    public void M() { }
    public int P => 1;
}
class D : IFoo
{
    public void B() { }
    public void M() { }
    public int P => 2;
}
class F
{
    bool A(object x) { return x is IFoo; }
    IFoo B2(object x) { return x as IFoo; }
    bool C2(object x) { return x is C; }
    bool D2(object x) { return x is IFoo && x is IBase; }
    bool E(object x) { return !(x is IFoo); }
    IFoo G(object x) { return x is IFoo ? (IFoo)x : null; }
}
""");
        Assert.True(result.Success);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}
