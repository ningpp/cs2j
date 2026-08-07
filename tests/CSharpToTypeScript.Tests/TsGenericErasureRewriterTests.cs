using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 24：泛型擦除降级（spec §2.6.2）——typeof(T)/default(T)/new T()/is T/(T)x。
/// 语法树层 TsGenericErasureRewriter 做语义决策（类型参数识别、工厂参数注入与调用点同步），
/// IR 层 TsGenericErasureIrRewriter 做类字面量/lambda 箭头/类型参数强转的文本改写。
/// </summary>
public class TsGenericErasureRewriterTests : TsConversionTestBase
{
    [Fact]
    public void Default_Concrete_Type_Emits_Type_Specific_Zero()
    {
        var result = Convert("""
class F
{
    int A() { return default(int); }
    bool B() { return default(bool); }
    double C() { return default(double); }
    string D() { return default(string); }
    object E() { return default; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return 0;", result.GeneratedCode);
        Assert.Contains("return false;", result.GeneratedCode);
        Assert.Contains("return 0.0;", result.GeneratedCode);
        Assert.Contains("return null;", result.GeneratedCode);
        // 具体类型不需要 csDefault 标记
        Assert.DoesNotContain("csDefault", result.GeneratedCode);
        Assert.DoesNotContain("TypeToken", result.GeneratedCode);
    }

    [Fact]
    public void Default_TypeParameter_Emits_CsDefault_With_Diagnostic()
    {
        var result = Convert("""
class F
{
    T A<T>(T x) { return default(T); }
    T B<T>(T x) { T t = default; return t; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("csDefault(TypeToken.of(\"T\"))", result.GeneratedCode);
        // 前端 Java 运行时类参数机制不应出现（TS 非法形态）
        Assert.DoesNotContain("Class<T>", result.GeneratedCode);
        Assert.DoesNotContain("_cs2j_T", result.GeneratedCode);
        Assert.DoesNotContain("DefaultValue", result.GeneratedCode);
        AssertDiagnostic(result, "csDefault");
    }

    [Fact]
    public void Default_TypeParameter_Class_Constraint_Keeps_Null()
    {
        var result = Convert("""
class F
{
    T A<T>(T x) where T : class { return default(T); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return null;", result.GeneratedCode);
        Assert.DoesNotContain("csDefault", result.GeneratedCode);
        Assert.DoesNotContain("TypeToken", result.GeneratedCode);
    }

    [Fact]
    public void New_TypeParameter_Injects_Factory_Parameter_And_Call_Site()
    {
        var result = Convert("""
class Widget
{
    public Widget() {}
}
class F
{
    T A<T>() where T : new() { return new T(); }
    Widget B() { return A<Widget>(); }
}
""");
        Assert.True(result.Success);
        // 方法签名追加必选工厂参数，体内 new T() → _factory_T()
        Assert.Contains("a<T>(_factory_T: () => T): T", result.GeneratedCode);
        Assert.Contains("return _factory_T();", result.GeneratedCode);
        // 调用点追加 () => new Widget()（lambda 箭头经 IR 层 -> 改写）
        Assert.Contains("a(() => new Widget())", result.GeneratedCode);
        Assert.DoesNotContain("new Object()", result.GeneratedCode);
        AssertDiagnostic(result, "factory parameter");
    }

    [Fact]
    public void New_TypeParameter_Passes_Factory_Through_For_TypeParam_Arg()
    {
        var result = Convert("""
class F
{
    T A<T>() where T : new() { return B<T>(); }
    U B<U>() where U : new() { return new U(); }
}
""");
        Assert.True(result.Success);
        // A 传递 B<T>()，自身也需工厂参数并透传给 B
        Assert.Contains("a<T>(_factory_T: () => T): T", result.GeneratedCode);
        Assert.Contains("return b(_factory_T);", result.GeneratedCode);
        Assert.Contains("b<U>(_factory_U: () => U): U", result.GeneratedCode);
        Assert.Contains("return _factory_U();", result.GeneratedCode);
    }

    [Fact]
    public void New_TypeParameter_Generic_Class_Method_Gets_Factory()
    {
        var result = Convert("""
class Widget
{
    public Widget() {}
}
class Box<T> where T : new()
{
    public T Make() { return new T(); }
}
class F
{
    Widget A() { return new Box<Widget>().Make(); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("make(_factory_T: () => T): T", result.GeneratedCode);
        Assert.Contains("new Box<Widget>().make(() => new Widget())", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_TypeParameter_Emits_TypeToken_With_Diagnostic()
    {
        var result = Convert("""
class F
{
    object A<T>() { return typeof(T); }
    string B<T>() { return typeof(T).Name; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("TypeToken.of(\"T\")", result.GeneratedCode);
        Assert.Contains("TypeToken.nameOf(\"T\")", result.GeneratedCode);
        Assert.DoesNotContain("TypeHelper", result.GeneratedCode);
        Assert.DoesNotContain("Class<T>", result.GeneratedCode);
        AssertDiagnostic(result, "TypeToken");
    }

    [Fact]
    public void TypeOf_Concrete_Class_Emits_Class_Reference()
    {
        var result = Convert("""
class Widget { }
class F
{
    object A() { return typeof(Widget); }
    string B() { return typeof(Widget).Name; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return Widget;", result.GeneratedCode);
        Assert.Contains("return Widget.name;", result.GeneratedCode);
        Assert.DoesNotContain(".class", result.GeneratedCode);
        Assert.DoesNotContain(".getName()", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_Bcl_Type_Emits_Runtime_Constructor_Reference()
    {
        var result = Convert("""
class F
{
    string A() { return typeof(string).Name; }
    string B() { return typeof(int).Name; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("String.name", result.GeneratedCode);
        Assert.Contains("Number.name", result.GeneratedCode);
        Assert.DoesNotContain(".class", result.GeneratedCode);
    }

    [Fact]
    public void Is_TypeParameter_Emits_TypeToken_IsInstance_With_Diagnostic()
    {
        var result = Convert("""
class F
{
    bool A<T>(object x) { return x is T; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("TypeToken.isInstance(\"T\", x)", result.GeneratedCode);
        Assert.DoesNotContain("instanceof", result.GeneratedCode);
        AssertDiagnostic(result, "isInstance");
    }

    [Fact]
    public void Cast_TypeParameter_Emits_As()
    {
        var result = Convert("""
class F
{
    T A<T>(object x) { return (T)x; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return (x as T);", result.GeneratedCode);
        Assert.DoesNotContain("(T)(x)", result.GeneratedCode);
    }

    [Fact]
    public void Cast_Concrete_Class_Emits_As()
    {
        var result = Convert("""
class Widget { }
class F
{
    Widget A(object x) { return (Widget)x; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return (x as Widget);", result.GeneratedCode);
        Assert.DoesNotContain("(Widget)(x)", result.GeneratedCode);
    }

    [Fact]
    public void Generic_Erasure_Sample_Compiles_L3()
    {
        var result = Convert("""
class Widget
{
    public Widget() {}
}
class Box<T> where T : new()
{
    public T Make() { return new T(); }
}
class F
{
    T A<T>(T x) where T : new() { return new T(); }
    string B<T>() { return typeof(T).Name; }
    object C<T>() { return typeof(T); }
    T D<T>(T x) { return default(T); }
    bool E<T>(object x) { return x is T; }
    T G<T>(object x) { return (T)x; }
    Widget H() { return this.A<Widget>(new Widget()); }
    Widget I() { return new Box<Widget>().Make(); }
    object J() { return typeof(Widget); }
    string K() { return typeof(Widget).Name; }
}
""");
        Assert.True(result.Success);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}
