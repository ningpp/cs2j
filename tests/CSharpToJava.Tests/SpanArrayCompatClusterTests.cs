using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the ValueListBuilder/SegmentStringBuilder error cluster:
/// 1. Array.Empty&lt;T&gt;() with a generic type argument must not emit illegal
///    generic array creation (new ReadOnlyMemory&lt;Character&gt;[0]).
/// 2. ArrayPool&lt;T&gt;.Shared.Rent/Return must map to the compat ArrayPool class.
/// 3. Assigning a T[] to a Span&lt;T&gt; target must wrap the array in new Span&lt;&gt;().
/// Reproduces javac errors "创建泛型数组", "找不到符号 ArrayPool", and
/// "T[]无法转换为Span&lt;T&gt;" in ValueListBuilder.java/SegmentStringBuilder.java.
/// </summary>
public class SpanArrayCompatClusterTests
{
    [Fact]
    public void ArrayEmpty_WithGenericElement_DoesNotEmitGenericArrayCreation()
    {
        var result = Convert(@"
class Holder
{
    private System.ReadOnlyMemory<char>[] _array;

    public static Holder Create()
    {
        var h = new Holder();
        h._array = System.Array.Empty<System.ReadOnlyMemory<char>>();
        return h;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Java forbids generic array creation; the element type must be raw.
        Assert.DoesNotContain("new ReadOnlyMemory<Character>[", code);
    }

    [Fact]
    public void ArrayPool_MapsToCompatClass()
    {
        var result = Convert(@"
class Pool
{
    private string[] _arrayFromPool;

    void Grow()
    {
        string[] array = System.Buffers.ArrayPool<string>.Shared.Rent(16);
        if (_arrayFromPool != null)
        {
            System.Buffers.ArrayPool<string>.Shared.Return(_arrayFromPool);
        }
        _arrayFromPool = array;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("import io.github.ningpp.compat.ArrayPool", code);
        Assert.Contains("ArrayPool.getShared().rent(16)", code);
    }

    [Fact]
    public void ArrayAssignedToSpan_IsWrappedInNewSpan()
    {
        var result = Convert(@"
class Buffer
{
    private System.Span<int> _span;
    private int[] _arrayFromPool;

    private void Grow()
    {
        int[] array = new int[8];
        _span = _arrayFromPool = array;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // T[] → Span<T> implicit conversion must become new Span<>(array).
        Assert.Contains("_span = new Span<>(", code);
        Assert.DoesNotContain("_span = _arrayFromPool = array", code);
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
