using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// 生成的集合类必须满足 compat 集合接口（CSharpIList/CSharpCollection/
/// CSharpGenericIterable）的擦除成员形状。C# 成员按自身声明直译后与接口成员
/// 同擦除签名却互不覆盖，javac 报“名称冲突 / 未覆盖抽象方法 / 返回类型不兼容”
/// （真实链 AGL Set&lt;T&gt;、RbTree&lt;T&gt;、NodeCollection）。生成器按接口形状
/// 统一改写：形参擦除为 Object（体内补强转）、C# void Add → int、iterator()
/// 收为 raw，并补齐 CSharpCollection 的 getSyncRoot/getIsSynchronized。
/// </summary>
public class CompatCollectionConformanceTests
{
    [Fact]
    public void ICollectionImplementor_AdoptsCompatInterfaceMemberShapes()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

class MyColl<T> : ICollection<T>
{
    private readonly List<T> _items = new List<T>();

    public void Add(T item) { _items.Add(item); }
    public bool Contains(T item) { return _items.Contains(item); }
    public bool Remove(T item) { return _items.Remove(item); }
    public void CopyTo(T[] array, int arrayIndex) { _items.CopyTo(array, arrayIndex); }
    public int Count { get { return _items.Count; } }
    public bool IsReadOnly { get { return false; } }
    public IEnumerator<T> GetEnumerator() { return _items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator() { return _items.GetEnumerator(); }
}");

        var code = result.GeneratedCode;
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---\n" + code);

        // C# void Add(T) → CSharpIList.add(Object) 返回插入位置。
        Assert.Contains("int add(Object", code, StringComparison.Ordinal);
        // C# bool Contains(T)/Remove(T) → Object 形参（元素擦除）。
        Assert.Contains("boolean contains(Object", code, StringComparison.Ordinal);
        Assert.Contains("boolean remove(Object", code, StringComparison.Ordinal);
        // CSharpCollection 要求 SyncRoot/IsSynchronized；C# ICollection<T> 无对应成员。
        Assert.Contains("public Object getSyncRoot()", code, StringComparison.Ordinal);
        Assert.Contains("public boolean getIsSynchronized()", code, StringComparison.Ordinal);
        // CSharpCollection.iterator() 的元素类型擦除为 Object，声明类型实参无法实现。
        Assert.Contains("public CSharpGenericEnumerator iterator()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpGenericEnumerator<T> iterator()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumerableImplementor_ErasesTypedRemoveParameter()
    {
        var result = Convert(@"
using System.Collections.Generic;

class MyBag<T> : IEnumerable<T>
{
    private readonly List<T> _items = new List<T>();

    public bool Remove(T item) { return _items.Remove(item); }
    public IEnumerator<T> GetEnumerator() { return _items.GetEnumerator(); }
}");

        var code = result.GeneratedCode;
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---\n" + code);

        // CSharpGenericIterable<T>.remove(Object)：bool Remove(T) 必须擦除形参才能覆盖。
        Assert.Contains("boolean remove(Object", code, StringComparison.Ordinal);
        // CSharpGenericIterable<T> 分支的元素类型保留：iterator() 仍是强类型。
        Assert.Contains("CSharpGenericEnumerator<T> iterator()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumerableImplementor_NonBooleanRemove_EmitsBooleanBridge()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Node
{
}

class MyTree<T> : IEnumerable<T>
{
    public Node Remove(T item) { return null; }
    public IEnumerator<T> GetEnumerator() { return null; }
}");

        var code = result.GeneratedCode;
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---\n" + code);

        // 返回被删节点的 C# Remove 无法覆盖 Collection.remove(Object)：强类型方法改名，
        // 另发 boolean 桥满足接口（C# 语义：删除成功即返回非 null）。
        Assert.Contains("boolean remove(Object", code, StringComparison.Ordinal);
        Assert.Contains("remove_2(", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
