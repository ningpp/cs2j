using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// json 测试模块第二轮收敛（w32）的四条通用规则：
/// ① record 形状与访问器一致（有附加属性/字段的 record 发射成不可变类，用 bean getter）；
/// ② C# 数组协变按目标数组类型分配（CSharpList&lt;T&gt;[] → CSharpIList[]）；
/// ③ 显式类型实参的泛型调用作为接收者时强转回 C# 结果类型；
/// ④ 非泛型 IDictionary.Keys/Values（声明为 CSharpICollection&lt;?&gt;）不再被包成 CSharpIList。
/// </summary>
public class ConvertedJsonShapeRuleTests
{
    [Fact]
    public void RecordWithExtraMembers_UsesBeanGetter_NotComponentAccessor()
    {
        var code = Convert("""
            record Pure(int Prop);

            record WithBody(int Age)
            {
                public string AgeText { get; set; } = Age.ToString();
            }

            class Use
            {
                string M(Pure a, WithBody b) => a.Prop.ToString() + b.Age.ToString();
            }
            """);

        // 纯位置 record → Java record，组件访问器 prop()
        Assert.Contains("a.prop()", code, StringComparison.Ordinal);
        // 有附加属性 → 不可变类 + bean getter，不能发组件访问器 age()
        Assert.Contains("b.getAge()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("b.age()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayCovariance_CreatesTargetElementArray()
    {
        var code = Convert("""
            using System.Collections;
            using System.Collections.Generic;

            class Use
            {
                void M()
                {
                    ICollection[] input = new List<int>[2];
                    input[0] = new List<int> { 1, 2 };
                }
            }
            """);

        Assert.Contains("new CSharpIList[2]", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(CSharpList<Integer>[])", code, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericInvocationReceiver_IsCastBackToResultType()
    {
        var code = Convert("""
            class Helper
            {
                public static T Deserialize<T>(string json) => default(T);
            }

            class Use
            {
                void M()
                {
                    int length = Helper.Deserialize<string>("[]").Length;
                    int first = Helper.Deserialize<int[]>("[1]")[0];
                }
            }
            """);

        // Java 把泛型调用推断成 Object：成员/元素访问前必须强转回 C# 结果类型。
        Assert.Contains("((String) Helper.deserialize(\"[]\")).length()", code, StringComparison.Ordinal);
        Assert.Contains("((int[]) Helper.deserialize(\"[1]\"))[0]", code, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericElementArrayCovariance_KeepsRawAllocation()
    {
        var code = Convert("""
            using System.Collections.Generic;

            class Use
            {
                void M()
                {
                    IList<int>[] lists = new List<int>[2];
                    IEnumerable<int>[] sequences = new List<int>[2];
                }
            }
            """);

        // 泛型元素不能 `new`：保持 `(T[]) new Raw[]` 强转（再由 IList 适配视图包装）。
        Assert.DoesNotContain("new CSharpIList<Integer>[", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new CSharpGenericIterable<Integer>[", code, StringComparison.Ordinal);
        Assert.Contains("CSharpIList.fromArray(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void NonGenericIDictionaryKeysValues_UseCollectionCarrier_NotIList()
    {
        var code = Convert("""
            using System.Collections;

            class Dict : IDictionary
            {
                private readonly Hashtable _inner = new Hashtable();

                public object this[object key] { get => _inner[key]; set => _inner[key] = value; }
                public ICollection Keys => _inner.Keys;
                public ICollection Values => _inner.Values;
                public int Count => _inner.Count;
                public bool IsReadOnly => _inner.IsReadOnly;
                public bool IsFixedSize => _inner.IsFixedSize;
                public bool IsSynchronized => _inner.IsSynchronized;
                public object SyncRoot => _inner.SyncRoot;

                public void Add(object key, object value) => _inner.Add(key, value);
                public void Clear() => _inner.Clear();
                public bool Contains(object key) => _inner.Contains(key);
                public void CopyTo(System.Array array, int index) => _inner.CopyTo(array, index);
                public IDictionaryEnumerator GetEnumerator() => _inner.GetEnumerator();
                public void Remove(object key) => _inner.Remove(key);
            }
            """);

        Assert.Contains("CSharpICollection<?> getKeys()", code, StringComparison.Ordinal);
        Assert.Contains("CSharpICollection<?> getValues()", code, StringComparison.Ordinal);
        // 声明载体是 CSharpICollection<?>：getKeys()/getValues() 的泛型目标槽位直接给出 T，
        // 不能再包 CSharpIList.from(...)（两个 compat 家族不兼容）。
        Assert.DoesNotContain("CSharpIList.from(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertThrows_UnresolvedXunitReceiver_MapsToThrows_NotKeywordEscape()
    {
        var code = Convert("""
            using System;
            using Xunit;

            class JsonException : Exception { }

            class Use
            {
                void M()
                {
                    Assert.Throws<JsonException>(() =>
                    {
                        var value = new object();
                    });
                }
            }
            """);

        // Throws 的 camelCase 是 Java 关键字 throws，默认会转义成 throwsValue；
        // 方法映射表（Xunit.Assert.Throws → throws_）必须在符号缺失时仍生效。
        Assert.Contains("Assert.throws_(JsonException.class", code, StringComparison.Ordinal);
        Assert.DoesNotContain("throwsValue", code, StringComparison.Ordinal);
    }

    private static string Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions { PreferStreamApi = false },
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.GeneratedCode;
    }
}
