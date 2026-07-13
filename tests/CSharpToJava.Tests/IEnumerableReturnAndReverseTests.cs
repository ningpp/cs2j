using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for IEnumerable&lt;T&gt; → CSharpGenericIterable&lt;T&gt; compatibility:
///  - Reverse() result passed to an IEnumerable&lt;T&gt; parameter must be cast to
///    CSharpGenericIterable (not java.lang.Iterable, which is not assignable).
///  - Returning a ternary of interface-typed IEnumerable&lt;T&gt; values (whose static type is the
///    IEnumerable&lt;T&gt; interface itself) from an IEnumerable&lt;T&gt;-returning member must be wrapped
///    in CSharpGenericIterable.from(...).
///  - Method-syntax GroupBy on an array must use Collectors.groupingBy(..., CSharpList.toCSharpList())
///    so the Map value type is CSharpList&lt;V&gt;, matching IGrouping&lt;K,V&gt; → Map.Entry&lt;K, CSharpList&lt;V&gt;&gt;.
/// </summary>
public class IEnumerableReturnAndReverseTests
{
    private readonly ITestOutputHelper _out;
    public IEnumerableReturnAndReverseTests(ITestOutputHelper output) { _out = output; }

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

    [Fact]
    public void Reverse_ResultPassedToIEnumerableParam_UsesCSharpGenericIterableCast()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    IEnumerable<int> GetItems() { return null; }
    void M() {
        var items = GetItems();
        Process(items.Reverse());
    }
    void Process(IEnumerable<int> items) { }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED: " + string.Join("\n", r.Diagnostics));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var code = r.GeneratedCode ?? "";
        // java.lang.Iterable<T> is NOT a CSharpGenericIterable<T> and must not be used as a cast target.
        Assert.DoesNotContain("(Iterable<", code);
        // The Reverse() result must be compatible with CSharpGenericIterable<T>.
        Assert.Contains("CSharpGenericIterable<", code);
    }

    [Fact]
    public void Return_TernaryOfInterfaceEnumerables_WrapsInCSharpGenericIterable()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    IEnumerable<int> GetA() { return null; }
    IEnumerable<int> GetB() { return null; }
    IEnumerable<int> M(bool cond) {
        return cond ? GetA() : GetB();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED: " + string.Join("\n", r.Diagnostics));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var code = r.GeneratedCode ?? "";
        // The ternary's static type is the IEnumerable<T> interface itself; it must be wrapped.
        Assert.Contains("CSharpGenericIterable.from(", code);
        Assert.DoesNotContain("(Iterable<", code);
    }

    [Fact]
    public void Return_TernaryWithTypeParameterEmptyArray_AdaptsToEmptyIterable()
    {
        // Regression for AGL Drawing RTree.GetAllLeaves: when one branch of an
        // IEnumerable<T>-returning ternary is `new T[0]` inside a generic type parameter,
        // the converter emits `(T[]) TypeHelper.newArrayInstance(tClass, 0)`. That raw
        // array is not assignable to Iterable<? extends T>, so it must be adapted to an
        // empty CSharpGenericIterable instead.
        var r = Convert(@"
using System.Collections.Generic;
class Sample<T> {
    IEnumerable<T> GetA() { return null; }
    T[] GetArray(int n) { return new T[n]; }
    IEnumerable<T> M(bool cond) {
        return cond ? GetA() : new T[0];
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED: " + string.Join("\n", r.Diagnostics));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var code = r.GeneratedCode ?? "";
        Assert.DoesNotContain("TypeHelper.newArrayInstance(tClass, 0)", code);
        Assert.Contains("CSharpGenericIterable.from(Collections.emptyList())", code);
    }

    [Fact]
    public void GroupBy_OnArray_WithMethodGroupKeySelector_UsesCSharpListDownstreamCollector()
    {
        // A method-group key selector (not a lambda) is not desugared by the LINQ rewriter,
        // so GroupBy is translated by InvocationExpressionTransformer into
        // Arrays.stream(arr).collect(Collectors.groupingBy(...)). The downstream collector
        // must be CSharpList.toCSharpList() so the Map value type is CSharpList<V>,
        // matching IGrouping<K,V> → Map.Entry<K, CSharpList<V>>.
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    int KeyOf(int n) => n % 2;
    void M(int[] numbers) {
        foreach (var g in numbers.GroupBy(KeyOf)) {
            var key = g.Key;
        }
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED: " + string.Join("\n", r.Diagnostics));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var code = r.GeneratedCode ?? "";
        // Method-syntax GroupBy on an array streams into groupingBy; the downstream collector
        // must be CSharpList.toCSharpList() so the Map value type is CSharpList<V>.
        Assert.Contains("Collectors.groupingBy(", code);
        Assert.Contains("CSharpList.toCSharpList()", code);
    }
}
