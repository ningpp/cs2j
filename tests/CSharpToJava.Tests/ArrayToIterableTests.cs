using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue #4: incompatible types - Point[] cannot be converted to java.lang.Iterable.
///
/// Root cause: C# arrays implicitly implement IEnumerable&lt;T&gt;, ICollection&lt;T&gt;, IList&lt;T&gt;, etc.
/// When converting a local variable declared as IEnumerable&lt;T&gt; and initialized with an array,
/// the converter mapped IEnumerable&lt;T&gt; → "Iterable&lt;T&gt;" then lowered javaType to "var"
/// (to let Java infer the concrete type from the initializer). However, the array-wrapping
/// check ran before javaType was lowered to "var", so no wrapping was applied.
/// The generated Java code was `var pts = arr;` where Java infers Point[] — which is NOT
/// assignable to Iterable&lt;Point&gt;, causing a Java compilation error.
///
/// Fix: in the "var" block of StatementTransformer.TransformLocalDeclaration, detect when
/// the semantic declared type is IEnumerable-like and the initializer is an array, then
/// wrap with ArrayHelper.toList() (reference types) or Arrays.stream().boxed().collect() (primitives).
/// </summary>
public class ArrayToIterableTests
{
    [Fact]
    public void IEnumerableLocal_FromPrimitiveArray_WrapsWithStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(int[] arr)
    {
        IEnumerable<int> pts = arr;
    }
}");

        Assert.True(result.Success);
        // Primitive int[] needs boxing: Arrays.stream().boxed().collect(...)
        Assert.Contains("Arrays.stream(arr).boxed().collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableLocal_FromReferenceTypeArray_WrapsWithAsList()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(string[] arr)
    {
        IEnumerable<string> pts = arr;
    }
}");

        Assert.True(result.Success);
        // Reference type String[] → ArrayHelper.toList()
        Assert.Contains("ArrayHelper.toList(arr)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableLocal_FromStructArray_WrapsWithAsList()
    {
        var result = Convert(@"
using System.Collections.Generic;
struct Point { public int X, Y; }
class Sample
{
    void M(Point[] arr)
    {
        IEnumerable<Point> pts = arr;
    }
}");

        Assert.True(result.Success);
        // Struct arrays are reference-type arrays in Java → ArrayHelper.toList()
        Assert.Contains("ArrayHelper.toList(arr)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableLocal_FromNewArrayLiteral_WrapsWithAsList()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M()
    {
        IEnumerable<string> pts = new string[] { ""a"", ""b"" };
    }
}");

        Assert.True(result.Success);
        Assert.Contains("ArrayHelper.toList(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableArgument_FromArray_WrapsWithAsList()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void Consume(IEnumerable<string> items) { }
    void M(string[] arr) { Consume(arr); }
}");

        Assert.True(result.Success);
        // Method argument wrapping works when method symbol is resolved
        Assert.Contains("ArrayHelper.toList", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableReturn_FromArray_UsesLiveView()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    IEnumerable<string> GetItems(string[] arr) { return arr; }
}");

        Assert.True(result.Success);
        Assert.Contains("ArrayHelper.asListView", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDoubleWrap_WhenAlreadyWrapped()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void Consume(IEnumerable<string> items) { }
    void M(string[] arr) { Consume(arr); }
}");

        Assert.True(result.Success);
        // Must NOT double-wrap
        Assert.DoesNotContain("ArrayHelper.toList(ArrayHelper.toList(", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
