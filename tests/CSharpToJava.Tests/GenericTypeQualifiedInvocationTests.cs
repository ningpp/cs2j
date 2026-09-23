using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a C# static invocation written with an explicit generic type qualifier
/// (<c>Type&lt;T&gt;.Method(...)</c>) is erased to the raw Java type name
/// (<c>Type.method(...)</c>). Java forbids type arguments on a static qualifier, so
/// emitting <c>Type&lt;T&gt;.method(...)</c> produces "illegal start of type" in javac.
/// Reproduces ArraySortHelper.cs:333 (<c>ArraySortHelper&lt;T&gt;.IntrospectiveSort</c>),
/// SortedSet.TreeSubSet.cs:197 (<c>SortedSet&lt;T&gt;.Log2</c>) and
/// SortedSetEqualityComparer.cs:42 (<c>SortedSet&lt;T&gt;.SortedSetEquals</c>).
/// </summary>
public class GenericTypeQualifiedInvocationTests
{
    [Fact]
    public void StaticCallOnOpenGenericQualifier_ErasesTypeArguments()
    {
        var result = Convert(@"
class ArraySortHelper<T>
{
    internal static void IntrospectiveSort(T[] keys) { }
}

class GenericArraySortHelper<T>
{
    void M(T[] keys)
    {
        ArraySortHelper<T>.IntrospectiveSort(keys);
    }
}");

        Assert.True(result.Success, Diagnostics(result));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ArraySortHelper.introspectiveSort(keys)", code);
        Assert.DoesNotContain("ArraySortHelper<T>.introspectiveSort", code);
    }

    [Fact]
    public void StaticCallOnClosedGenericQualifier_ErasesTypeArguments()
    {
        var result = Convert(@"
class Info<T>
{
    public static T Make() => default(T);
}
class Holder
{
    void M()
    {
        var x = Info<int>.Make();
    }
}");

        Assert.True(result.Success, Diagnostics(result));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Info.make()", code);
        Assert.DoesNotContain("Info<Integer>.make()", code);
    }

    [Fact]
    public void StaticCallReturnedDirectly_ErasesTypeArguments()
    {
        var result = Convert(@"
class ArraySortHelper<T>
{
    internal static int InternalBinarySearch(T[] array, int index) => index;
}

class GenericArraySortHelper<T>
{
    int M(T[] array, int index)
    {
        return ArraySortHelper<T>.InternalBinarySearch(array, index);
    }
}");

        Assert.True(result.Success, Diagnostics(result));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ArraySortHelper.internalBinarySearch(array, index)", code);
        Assert.DoesNotContain("ArraySortHelper<T>.internalBinarySearch", code);
    }

    [Fact]
    public void InstanceReceiver_KeepsTypeArguments()
    {
        var result = Convert(@"
class Info<T>
{
    public T Value { get; set; }
}

class Holder
{
    void M(object parameterInfo)
    {
        var v = ((Info<int>)parameterInfo).Value;
    }
}");

        Assert.True(result.Success, Diagnostics(result));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("((Info<Integer>)(parameterInfo)).getValue()", code);
        Assert.DoesNotContain("((Info)(parameterInfo)).getValue()", code);
    }

    private static string Diagnostics(ConversionResult result)
        => string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode;

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
