using System.Text.RegularExpressions;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Round 22：C# IEnumerable&lt;T&gt;/ICollection&lt;T&gt; 实现类（生成端产 Java 风味
/// `iterator()` 桥接）在 TS 目标缺 `[Symbol.iterator]()`——for-of（TS2488）与
/// Iterable&lt;T&gt; 结构化实参（TS2741）与 implements Iterable&lt;T&gt;（TS2420）三族
/// 同根。生成端在含 public 实例 `iterator()` 的类上补发 `[Symbol.iterator]()`：
/// 返回 IterableIterator&lt;T&gt; 时直接委托；moveNext()/getCurrent() 风味时用
/// 适配器对象包装（probe22 验证 strict tsc 可编译）。
/// </summary>
public class TsSymbolIteratorRound22Tests : TsConversionTestBase
{
    private static readonly Regex IteratorMemberPattern = new(
        @"\[Symbol\.iterator\]\(\): IterableIterator<(?<elem>[^>]+)> \{", RegexOptions.Compiled);

    /// <summary>与真实工程 global.d.ts 同源的桩（CSharpList/CSharpGenericEnumerator 等
    /// Java 风味名；测试 compat 桩只覆盖 compat 包面）。</summary>
    private const string GlobalStub =
        """
        type CSharpList<T> = Array<T>;
        type CSharpGenericEnumerator<T> = { moveNext(): boolean; getCurrent(): T; };
        declare const CSharpGenericEnumerator: { from<T>(source: Iterable<T>): CSharpGenericEnumerator<T> };
        interface Array<T> {
            iterator(): IterableIterator<T>;
            push(...items: T[]): number;
        }
        """;

    [Fact]
    public void IEnumerable_Class_Emits_SymbolIterator_Delegating_To_Iterator()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Boxes : IEnumerable<int>
{
    private List<int> items = new List<int>();
    public IEnumerator<int> GetEnumerator() { return items.GetEnumerator(); }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
    public int Sum()
    {
        int s = 0;
        foreach (int x in this) s = s + x;
        return s;
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var m = IteratorMemberPattern.Match(result.GeneratedCode);
        Assert.True(m.Success, $"Missing [Symbol.iterator] member in:\n{result.GeneratedCode}");
        Assert.Equal("number", m.Groups["elem"].Value);
        Assert.Contains("[Symbol.iterator]() {", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void Yield_Generator_Iterator_Delegates_Directly()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Range : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator()
    {
        for (int i = 0; i < 3; i = i + 1) yield return i;
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
    public int Sum()
    {
        int s = 0;
        foreach (int x in this) s = s + x;
        return s;
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var m = IteratorMemberPattern.Match(result.GeneratedCode);
        Assert.True(m.Success, $"Missing [Symbol.iterator] member in:\n{result.GeneratedCode}");
        Assert.Equal("number", m.Groups["elem"].Value);
        Assert.Contains("return this.iterator() as IterableIterator<number>;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }

    [Fact]
    public void Generic_Class_Element_Type_Is_Type_Parameter()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Box<T> : IEnumerable<T>
{
    private List<T> items = new List<T>();
    public IEnumerator<T> GetEnumerator() { return items.GetEnumerator(); }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
    public void AddAll(Box<T> other)
    {
        foreach (T x in other) items.Add(x);
    }
}
""");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var m = IteratorMemberPattern.Match(result.GeneratedCode);
        Assert.True(m.Success, $"Missing [Symbol.iterator] member in:\n{result.GeneratedCode}");
        Assert.Equal("T", m.Groups["elem"].Value);
        AssertCompilesWithCompat(result.GeneratedCode, "global.d.ts", GlobalStub);
    }
}
