using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：索引器发射（spec §1.4.3/§5.3）。用户类型索引器声明 → get_Item/set_Item
/// 方法（IndexerTransformer TS 分叉，避免 SafeIdentifier 把 get/set 转义为 get$/set$）；
/// 调用点 b[i] → b.get_Item(i)、b[i] = v → b.set_Item(i, v)、复合赋值与自增 → 展开
/// （TsIndexerRewriter 语法层改写，BCL 集合/数组/string 索引器保持原路径）。
/// 全部用例经 L3 tsc strict 编译。
/// </summary>
public class TsIndexerTests : TsConversionTestBase
{
    [Fact]
    public void Indexer_Emits_GetItem_SetItem_And_Rewrites_CallSites()
    {
        var result = Convert("""
public class Bag
{
    private int[] items = new int[4];
    public int this[int i] { get { return items[i]; } set { items[i] = value; } }
    public int Read(Bag b, int i) { return b[i]; }
    public void Write(Bag b, int i, int v) { b[i] = v; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public get_Item(i: number): number", result.GeneratedCode);
        Assert.Contains("public set_Item(i: number, value: number): number", result.GeneratedCode);
        Assert.Contains("return b.get_Item(i);", result.GeneratedCode);
        Assert.Contains("b.set_Item(i, v);", result.GeneratedCode);
        Assert.DoesNotContain("get$", result.GeneratedCode);
        Assert.DoesNotContain("set$", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Compound_Assignment_And_Increment_Are_Expanded()
    {
        var result = Convert("""
public class Bag
{
    private int[] items = new int[4];
    public int this[int i] { get { return items[i]; } set { items[i] = value; } }
    public void Inc(Bag b, int i) { b[i] = b[i] + 1; }
    public void Add(Bag b, int i, int v) { b[i] += v; }
    public void Post(Bag b, int i) { b[i]++; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("b.set_Item(i, b.get_Item(i) + 1);", result.GeneratedCode);
        Assert.Contains("b.set_Item(i, b.get_Item(i) + (v));", result.GeneratedCode);
        Assert.Contains("b.set_Item(i, b.get_Item(i) + 1);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }

    [Fact]
    public void Interface_Indexer_Emits_Methods_And_CallSites()
    {
        var result = Convert("""
public interface IStore
{
    int this[int i] { get; set; }
}
public class Store : IStore
{
    private int[] items = new int[2];
    public int this[int i] { get { return items[i]; } set { items[i] = value; } }
}
public class Use
{
    public int Read(IStore s, int i) { return s[i]; }
    public void Write(IStore s, int i, int v) { s[i] = v; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("get_Item(i: number): number", result.GeneratedCode);
        Assert.Contains("set_Item(i: number, value: number): number", result.GeneratedCode);
        Assert.Contains("return s.get_Item(i);", result.GeneratedCode);
        Assert.Contains("s.set_Item(i, v);", result.GeneratedCode);
        // 接口伴随探针 is$IStore 依赖 CsRuntime → compat 桩
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Bcl_Collection_Indexer_Is_Not_Rewritten()
    {
        var result = Convert("""
using System.Collections.Generic;
public class Use
{
    public int Read(List<int> xs, int i) { return xs[i]; }
}
""");
        Assert.True(result.Success);
        Assert.DoesNotContain("get_Item", result.GeneratedCode);
    }

    [Fact]
    public void ExpressionBody_Indexer_Emits_GetItem()
    {
        var result = Convert("""
public class Box
{
    private int[] items = new int[2];
    public int this[int i] => items[i];
    public int Read(Box b, int i) { return b[i]; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public get_Item(i: number): number", result.GeneratedCode);
        Assert.Contains("return b.get_Item(i);", result.GeneratedCode);
        AssertCompiles(result.GeneratedCode);
    }
}