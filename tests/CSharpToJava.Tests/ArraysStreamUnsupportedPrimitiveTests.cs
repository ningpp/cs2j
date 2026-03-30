using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the Arrays.stream unsupported primitive array types issue.
///
/// Java's Arrays.stream only supports int[], long[], double[], and T[].
/// It does NOT support short[], byte[], char[], float[], or boolean[].
/// The converter must use IntStream.range-based alternatives for these types.
/// All call sites that produce Arrays.stream must be unified through central helpers.
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

    [Fact]
    public void LinqOnBoolArray_UsesIntStreamRange_NotArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(bool[] arr)
    {
        var x = arr.Where(v => v).ToArray();
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

    // ── Any() on arrays ──────────────────────────────────────────────────

    [Fact]
    public void AnyOnShortArray_UsesLength_NotArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    bool M(short[] arr) => arr.Any();
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("arr.length > 0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyOnIntArray_UsesLength_NotArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    bool M(int[] arr) => arr.Any();
}");

        Assert.True(result.Success);
        Assert.Contains("arr.length > 0", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Array.ForEach ────────────────────────────────────────────────────

    [Fact]
    public void ArrayForEachOnShortArray_UsesIntStreamRange_NotArraysStream()
    {
        var result = Convert(@"
using System;
class Sample
{
    void M(short[] arr) { Array.ForEach(arr, x => Console.WriteLine(x)); }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".forEach(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayForEachOnIntArray_StillUsesArraysStream()
    {
        var result = Convert(@"
using System;
class Sample
{
    void M(int[] arr) { Array.ForEach(arr, x => Console.WriteLine(x)); }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".forEach(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Method argument wrapping ─────────────────────────────────────────

    [Fact]
    public void MethodArgShortArray_ToIEnumerable_UsesIntStreamRange()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample
{
    void Consume(IEnumerable<short> items) { }
    void M(short[] arr) { Consume(arr); }
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

    [Fact]
    public void LinqOnStringArray_StillUsesArraysStream()
    {
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(string[] arr)
    {
        var x = arr.Where(v => v != null).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── Reference-type arrays must NOT use mapToObj / boxed ────────────────

    [Fact]
    public void SelectOnReferenceTypeArray_UsesMap_NotMapToObj()
    {
        var result = Convert(@"
using System.Linq;
class Edge { public string Name { get; set; } }
class Sample
{
    void M(Edge[] edges)
    {
        var names = edges.Select(e => e.Name).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.stream(edges)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".map(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("mapToObj", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainedLinqOnReferenceTypeArray_UsesMap_NotMapToObj()
    {
        var result = Convert(@"
using System.Linq;
class Edge { public string Name { get; set; } public bool Active { get; set; } }
class Sample
{
    void M(Edge[] edges)
    {
        var names = edges.Where(e => e.Active).Select(e => e.Name).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("Arrays.stream(edges)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".map(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("mapToObj", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".boxed()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectOnIntArray_ReturningObject_UsesMapToObj()
    {
        // int[] → IntStream; Select producing string → mapToObj is CORRECT here
        var result = Convert(@"
using System.Linq;
class Sample
{
    void M(int[] arr)
    {
        var strs = arr.Select(x => x.ToString()).ToArray();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("mapToObj", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ── No generated code should contain Arrays.stream(unsupported) patterns ──

    [Fact]
    public void GeneratedCode_NeverContains_ArraysStreamOnBoolArray()
    {
        // Any code that uses bool[] with LINQ or IEnumerable should NOT produce Arrays.stream(boolArr)
        var result = Convert(@"
using System.Linq;
using System.Collections.Generic;
class Sample
{
    bool M(bool[] arr) => arr.Any();
    void N(bool[] arr) { IEnumerable<bool> x = arr; }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("Arrays.stream(arr)", result.GeneratedCode, StringComparison.Ordinal);
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
