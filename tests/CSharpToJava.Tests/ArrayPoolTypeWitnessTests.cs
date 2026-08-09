using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ArrayPool&lt;T&gt;.Shared.Rent/Return map to the compat ArrayPool class.
/// Rent emits a runtime component Class (ArrayPool.rentShared(T.class, n)) so the
/// allocated array is properly typed and the caller's assignment to T[] does not
/// throw ClassCastException at runtime — bare ArrayPool.getShared().rent(n) can only
/// produce Object[] because Java erases the generic type argument. Return keeps the
/// typed-pool form (ArrayPool.&lt;T&gt;getShared().returnValue(...)). Primitive char
/// routes to the shared char[] pool because Java generics cannot express ArrayPool&lt;char&gt;.
/// Reproduces ValueListBuilder/ValueStringBuilder errors.
/// </summary>
public class ArrayPoolTypeWitnessTests
{
    [Fact]
    public void ArrayPoolRent_EmitsRuntimeComponentClass_ForReferenceElement()
    {
        var result = Convert(@"
class Pool
{
    private string[] _arrayFromPool;

    void Grow()
    {
        string[] array = System.Buffers.ArrayPool<string>.Shared.Rent(16);
        System.Buffers.ArrayPool<string>.Shared.Return(_arrayFromPool);
        _arrayFromPool = array;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ArrayPool.rentShared(String.class, 16)", code);
        Assert.Contains("ArrayPool.<String>getShared().returnValue(_arrayFromPool)", code);
    }

    [Fact]
    public void ArrayPoolRent_EmitsRuntimeComponentClass_ForTypeParameter()
    {
        var result = Convert(@"
class Builder<T>
{
    private T[] _arrayFromPool;

    private void Grow(int size)
    {
        T[] array = System.Buffers.ArrayPool<T>.Shared.Rent(size * 2);
        _arrayFromPool = array;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ArrayPool.rentShared(T.class, size * 2)", code);
    }

    [Fact]
    public void ArrayPoolRent_RoutesCharToSharedCharArrayPool()
    {
        var result = Convert(@"
class CharPool
{
    private char[] _arrayToReturnToPool;

    public CharPool(int initialCapacity)
    {
        _arrayToReturnToPool = System.Buffers.ArrayPool<char>.Shared.Rent(initialCapacity);
    }

    public void Dispose()
    {
        System.Buffers.ArrayPool<char>.Shared.Return(_arrayToReturnToPool);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Java generics cannot express ArrayPool<char>; use the char[] pool helpers.
        Assert.Contains("ArrayPool.rentSharedCharArray(initialCapacity)", code);
        Assert.Contains("ArrayPool.returnSharedCharArray(_arrayToReturnToPool)", code);
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
