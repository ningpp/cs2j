using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：委托→函数类型发射（spec §3.1.3/§5.3）。自定义委托声明 →
/// `export type X = (p: T) => R` 函数类型别名（TsTypeWriter 识别 DelegateTransformer
/// 的 cs2j:TsDelegate 标记）；BCL 委托 Func&lt;T,R&gt;/Action&lt;T&gt;/Predicate&lt;T&gt;
/// 等在 IR 中为 Java 风味名（Function&lt;Integer, String&gt; 等），由
/// TsMemberMappingTable 的委托分支映射为函数类型（泛型实参逐段递归映射）；
/// 调用点 fn(v) 保持直调（InvocationExpressionTransformer TS 分叉：Java 需
/// .apply(...)，TS 函数值直接调用）。委托不生成接口运行时探针。全部用例经 L3。
/// </summary>
public class TsDelegateToFunctionTypeTests : TsConversionTestBase
{
    [Fact]
    public void Custom_Delegate_Emits_Function_Type_Alias()
    {
        var result = Convert("""
public delegate int Transformer(int x);
public delegate T Identity<T>(T v);
""");
        Assert.True(result.Success);
        Assert.Contains("export type Transformer = (x: number) => number;", result.GeneratedCode);
        Assert.Contains("export type Identity<T> = (v: T) => T;", result.GeneratedCode);
        // 函数类型别名非接口：不生成运行时探针
        Assert.DoesNotContain("is$Transformer", result.GeneratedCode);
        Assert.DoesNotContain("interface Transformer", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Bcl_Func_Action_Predicate_Map_To_Function_Types()
    {
        var result = Convert("""
using System;
public class Use
{
    public Func<int, string> F;
    public Action<int> A;
    public Predicate<string> P;
    public Func<int, int, int> Add2;
    public void UseFn(Func<int, int> fn, int v) { fn(v); }
}
""");
        Assert.True(result.Success);
        // Round 19：无初始化器委托字段不再宽化 `((...) => T) | null`——null! 保持函数类型
        Assert.Contains("public F: (a: number) => string = null!;", result.GeneratedCode);
        Assert.Contains("public A: (a: number) => void = null!;", result.GeneratedCode);
        Assert.Contains("public P: (a: string) => boolean = null!;", result.GeneratedCode);
        Assert.Contains("public Add2: (a: number, b: number) => number = null!;", result.GeneratedCode);
        Assert.Contains("public useFn(fn: (a: number) => number, v: number): void", result.GeneratedCode);
        Assert.DoesNotContain("Function<Integer", result.GeneratedCode);
        Assert.DoesNotContain("Consumer<Integer", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Delegate_CallSites_Stay_Direct_Calls()
    {
        var result = Convert("""
using System;
public delegate int Transformer(int x);
public class Use
{
    public Transformer T;
    public Func<int, int> F;
    public int Apply(Func<int, int> fn, int v) { return fn(v); }
    public int CallField() { return F(3); }
    public int CallThis() { return this.T(4); }
    public int CallProp(Box b) { return b.Fn(5); }
    public int CallInvoke() { return F.Invoke(6); }
}
public class Box
{
    public Func<int, int> Fn;
}
""");
        Assert.True(result.Success);
        Assert.Contains("return fn(v);", result.GeneratedCode);
        Assert.Contains("return this.F!(3);", result.GeneratedCode);
        Assert.Contains("return this.T!(4);", result.GeneratedCode);
        Assert.Contains("return b.Fn!(5);", result.GeneratedCode);
        Assert.Contains("return this.F!(6);", result.GeneratedCode);
        Assert.DoesNotContain(".apply(", result.GeneratedCode);
        Assert.DoesNotContain(".accept(", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Delegate_Construction_Passes_Lambda_Through()
    {
        var result = Convert("""
using System;
public delegate int Transformer(int x);
public class Use
{
    public Transformer Make() { return new Transformer(x => x * 2); }
    public Func<int, int> MakeFunc() { return new Func<int, int>(x => x + 1); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("return x => x * 2;", result.GeneratedCode);
        Assert.Contains("return x => x + 1;", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
}
