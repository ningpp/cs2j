using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：yield → generator 发射（spec §1.4.4/§5.3）。C#→Java 共享管线把含 yield
/// 的方法降级为“收集到列表”（CSharpList + _yieldResult.add + return），TsYieldRewriter
/// 在 IR 层改写为原生 generator：`*name(...): IterableIterator&lt;T&gt;` + `yield expr;`，
/// `yield break` → `return;`，属性 getter 用 IIFE generator 包裹（TS 无 generator accessor）。
/// 方法 Body 字符串不经过语句发射器，改写后整体过 TsRawStatementNormalizer
/// （经典 for 头 let 化等）。非 yield 的 IEnumerable&lt;T&gt; 返回（stream 链）归 M4 Task 36
/// LINQ 目标分叉。全部用例经 L3。
/// </summary>
public class TsYieldToGeneratorTests : TsConversionTestBase
{
    [Fact]
    public void YieldReturn_Emits_Generator_Method()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Use
{
    public IEnumerable<int> Range(int n)
    {
        for (int i = 0; i < n; i = i + 1) yield return i;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public *range(n: number): IterableIterator<number> {", result.GeneratedCode);
        Assert.Contains("for (let i: number = 0; i < n; i = i + 1) {", result.GeneratedCode);
        Assert.Contains("yield i;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void YieldBreak_Emits_Return_Inside_Guard()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Use
{
    public IEnumerable<int> Stop(int n)
    {
        for (int i = 0; i < n; i = i + 1)
        {
            if (i == 2) yield break;
            yield return i;
        }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public *stop(n: number): IterableIterator<number> {", result.GeneratedCode);
        Assert.Contains("if (i === 2) {", result.GeneratedCode);
        Assert.Contains("return;", result.GeneratedCode);
        Assert.Contains("yield i;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Yield_String_Elements_Maps_Element_Type()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Use
{
    public IEnumerable<string> Words()
    {
        yield return "a";
        yield return "b";
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public *words(): IterableIterator<string> {", result.GeneratedCode);
        Assert.Contains("yield \"a\";", result.GeneratedCode);
        Assert.DoesNotContain("_yieldResult", result.GeneratedCode);
        Assert.DoesNotContain("CSharpList", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void IEnumerator_Return_Also_Emits_Generator()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Use
{
    public IEnumerator<int> Iter()
    {
        yield return 1;
        yield return 2;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public *iter(): IterableIterator<number> {", result.GeneratedCode);
        Assert.Contains("yield 1;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Property_Getter_With_Yield_Emits_IIFE_Generator()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Use
{
    public IEnumerable<int> Getter
    {
        get { yield return 1; }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("get Getter(): IterableIterator<number> {", result.GeneratedCode);
        Assert.Contains("return (function* () {", result.GeneratedCode);
        Assert.Contains("yield 1;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Foreach_Over_Generator_Emits_ForOf()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Use
{
    public IEnumerable<int> Range(int n)
    {
        for (int i = 0; i < n; i = i + 1) yield return i;
    }
    public int Sum(int n)
    {
        int sum = 0;
        foreach (int x in Range(n)) sum = sum + x;
        return sum;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("for (const x of this.range(n)) {", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}
