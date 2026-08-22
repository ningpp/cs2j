using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that "IEnumerable&lt;T&gt; as T[]" produces null in Java because
/// Java arrays do not implement Iterable&lt;T&gt;, making the cast always impossible.
/// </summary>
public class EnumerableAsArrayTests
{
    [Fact]
    public void IEnumerableAsArray_EmitsNull()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;

class Sample {
    int[] M(IEnumerable<int> items) {
        return items as int[] ?? items.ToArray();
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // "items as int[]" should become "null" (not instanceof check)
        // because Java arrays don't implement Iterable
        Assert.DoesNotContain("instanceof", result.GeneratedCode);
    }

    [Fact]
    public void IEnumerableToArray_DoesNotUseArrayCopyHelper()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;

class Sample {
    int[] M(IEnumerable<int> items) {
        return items.ToArray();
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("StreamSupport.stream(CSharpGenericIterable.requireNonNull(items, \"source\").spliterator(), false).mapToInt(Integer::intValue).toArray()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ArrayHelper.toIntArray(items)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableReferenceArray_ToArrayContains_WrapsArrayInArraysStreamAnyMatch()
    {
        // Regression test for agl-master NudgingHelpersTest: enumerable.ToArray().Contains(x)
        // where the element type is a reference type (e.g. Point). The .ToArray() call is
        // materialized to a Point[] array in Java; .Contains must be emitted as
        // Arrays.stream(array).anyMatch(...) rather than array.anyMatch(...) (illegal).
        var source = @"
using System.Collections.Generic;
using System.Linq;

class Point {
    public int X { get; set; }
    public int Y { get; set; }
}

class Sample {
    bool HasTarget(IEnumerable<Point> items) {
        return items.ToArray().Contains(new Point { X = 5, Y = 1 });
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("Arrays.stream", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Arrays.stream(StreamSupport.stream(CSharpGenericIterable.requireNonNull(items, \"source\").spliterator(), false).toArray(Point[]::new)).anyMatch(_item -> Objects.equals(_item,", result.GeneratedCode, StringComparison.Ordinal);
        // The materialized Point[] array itself must NOT receive the anyMatch call directly.
        Assert.DoesNotContain(".toArray(Point[]::new).anyMatch(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerablePrimitiveArray_ToArrayContains_StillUsesBoxedCollectContains()
    {
        // Non-regression: primitive element arrays must keep the boxed().collect().contains()
        // form even when the receiver is a ToArray() invocation (an array value, not a stream).
        var source = @"
using System.Collections.Generic;
using System.Linq;

class Sample {
    bool HasTarget(IEnumerable<int> items) {
        return items.ToArray().Contains(5);
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("Arrays.stream", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("boxed().collect(Collectors.toSet()).contains(5)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".toArray().anyMatch(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".toArray().collect(", result.GeneratedCode, StringComparison.Ordinal);
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
