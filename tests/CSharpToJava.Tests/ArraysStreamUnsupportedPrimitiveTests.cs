using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the Arrays.stream unsupported primitive array types issue.
///
/// Java's Arrays.stream only supports int[], long[], double[], and T[].
/// It does NOT support short[], byte[], char[], float[], or boolean[].
/// The converter must use IntStream.range-based alternatives for these types.
/// </summary>
public class ArraysStreamUnsupportedPrimitiveTests
{
    // ── LINQ stream operations ─────────────────────────────────────────────

    [Fact]
    public void LinqOnShortArray_UsesIntStreamRange_NotArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(short[] arr)
    {
        var x = arr.Where(v => v > 0).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LinqOnByteArray_UsesIntStreamRange_NotArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(byte[] arr)
    {
        var x = arr.Where(v => v > 0).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LinqOnCharArray_UsesIntStreamRange_NotArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(char[] arr)
    {
        var x = arr.Where(v => v != 'a').ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LinqOnFloatArray_UsesIntStreamRange_NotArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(float[] arr)
    {
        var x = arr.Where(v => v > 0f).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── IEnumerable assignment wrapping ───────────────────────────────────

    [Fact]
    public void IEnumerableFromShortArray_UsesIntStreamRange_NotArraysStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(short[] arr)
    {
        IEnumerable<short> x = arr;
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableFromByteArray_UsesIntStreamRange_NotArraysStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(byte[] arr)
    {
        IEnumerable<byte> x = arr;
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableFromFloatArray_UsesIntStreamRange_NotArraysStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(float[] arr)
    {
        IEnumerable<float> x = arr;
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Supported types still use Arrays.stream ────────────────────────────

    [Fact]
    public void LinqOnIntArray_StillUsesArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(int[] arr)
    {
        var x = arr.Where(v => v > 0).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LinqOnLongArray_StillUsesArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(long[] arr)
    {
        var x = arr.Where(v => v > 0).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LinqOnDoubleArray_StillUsesArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(double[] arr)
    {
        var x = arr.Where(v => v > 0).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableFromIntArray_StillUsesArraysStream()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void M(int[] arr)
    {
        IEnumerable<int> x = arr;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
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
