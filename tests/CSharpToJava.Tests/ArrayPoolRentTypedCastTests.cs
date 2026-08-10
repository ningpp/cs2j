using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ArrayPool&lt;T&gt;.Shared.Rent results are cast to the typed
/// element array. rentShared(T.class, n) infers the raw element type (or
/// Object), so `var newCache = ...; typedArray = newCache;` fails with
/// "Object[] cannot be converted to Tuple5<...>[]". Reproduces
/// ObjectWithParameterizedConstructorConverter.Grow.
/// </summary>
public class ArrayPoolRentTypedCastTests
{
    [Fact]
    public void ArrayPoolRent_CastsResultToTypedArray()
    {
        var result = Convert(@"
class Holder
{
    private System.ValueTuple<System.Int32, System.String>[] _props;

    void Grow()
    {
        var newCache = System.Buffers.ArrayPool<System.ValueTuple<System.Int32, System.String>>.Shared.Rent(10);
        _props = newCache;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(Tuple2<Integer, String>[]) ArrayPool.rentShared(Tuple2.class, 10)", code);
        Assert.Contains("_props = newCache", code);
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
