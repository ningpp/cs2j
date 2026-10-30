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

    /// <summary>
    /// 真实链 XmlSchemaCollection.Add(XmlSchemaCollection) 的 C# 方法体带早退
    /// <c>if (ReferenceEquals(this, schema)) return;</c>。void→int 适配后裸 return
    /// 必须改成 <c>return 0;</c>，否则 javac 报“缺少返回值”。
    /// </summary>
    [Fact]
    public void CollectionImplementor_AddWithEarlyReturn_ReturnsValueOnEveryPath()
    {
        var result = Convert(@"
using System.Collections;

class MyColl : ICollection
{
    public void Add(MyColl other)
    {
        if (object.ReferenceEquals(this, other))
            return;
    }

    public int Count { get { return 0; } }
    public bool IsSynchronized { get { return false; } }
    public object SyncRoot { get { return this; } }
    public void CopyTo(System.Array array, int index) { }
    public IEnumerator GetEnumerator() { return null; }
}");

        var code = result.GeneratedCode;
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---\n" + code);

        Assert.Contains("int add(Object", code, StringComparison.Ordinal);
        // C# 早退的裸 return; 不能留在 void→int 适配后的方法体里。
        Assert.DoesNotContain("return;", code, StringComparison.Ordinal);
        Assert.Contains("return 0;", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// void RemoveAt(int) → Object removeAt(int) 同样把早退的裸 return; 适配为
    /// <c>return null;</c>。
    /// </summary>
    [Fact]
    public void CollectionImplementor_RemoveAtWithEarlyReturn_ReturnsValueOnEveryPath()
    {
        var result = Convert(@"
using System.Collections;

class MyList : IList
{
    public object this[int index] { get { return null; } set { } }
    public bool IsFixedSize { get { return false; } }
    public bool IsReadOnly { get { return false; } }
    public int Count { get { return 0; } }
    public object SyncRoot { get { return this; } }
    public bool IsSynchronized { get { return false; } }
    public int Add(object value) { return 0; }
    public void Clear() { }
    public bool Contains(object value) { return false; }
    public int IndexOf(object value) { return -1; }
    public void Insert(int index, object value) { }
    public void Remove(object value) { }
    public void RemoveAt(int index)
    {
        if (index < 0)
            return;
    }
    public void CopyTo(System.Array array, int index) { }
    public IEnumerator GetEnumerator() { return null; }
}");

        var code = result.GeneratedCode;
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---\n" + code);

        Assert.Contains("Object removeAt(int index)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return;", code, StringComparison.Ordinal);
        Assert.Contains("return null;", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// ICollection&lt;T&gt; 当前映射为 CSharpICollection&lt;T&gt;（经 AllInterfaces 的
    /// IEnumerable 走 CSharpGenericIterable 分支），void Add(T) → boolean add(T)；
    /// 早退裸 return; 同样必须补 <c>return true;</c>。
    /// </summary>
    [Fact]
    public void GenericCollectionImplementor_AddWithEarlyReturn_ReturnsValueOnEveryPath()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

class MyColl<T> : ICollection<T>
{
    private readonly List<T> _items = new List<T>();

    public void Add(T item)
    {
        if (object.ReferenceEquals(item, this))
            return;
        _items.Add(item);
    }
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

        Assert.Contains("boolean add(T item)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return;", code, StringComparison.Ordinal);
        Assert.Contains("return true;", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 真实链 StringFunctions 的私有辅助方法 <c>bool Contains(XPathNodeIterator)</c>：
    /// private C# 方法不是接口实现，形状对齐不能把它擦除成 <c>contains(Object)</c>，
    /// 否则与继承的 compat 默认方法同签名却降为 private（javac 弱化访问权限错误）。
    /// </summary>
    [Fact]
    public void EnumerableImplementor_PrivateContainsHelper_StaysTyped()
    {
        var result = Convert(@"
using System.Collections;

class StringFunctions : IEnumerable
{
    private bool Contains(string value) { return value != null; }

    public IEnumerator GetEnumerator() { return null; }
}");

        var code = result.GeneratedCode;
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---\n" + code);

        // 私有辅助方法保留强类型形参（合法重载），不得擦除为 Object。
        Assert.Contains("private boolean contains(String value)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("contains(Object", code, StringComparison.Ordinal);
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
