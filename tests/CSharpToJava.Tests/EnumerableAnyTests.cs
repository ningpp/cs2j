using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for IEnumerable.Any() conversion.
///
/// Bug fixed:
///   colls.Any() (no predicate) was incorrectly emitted as colls.anyMatch(),
///   which is invalid Java — anyMatch(Predicate) is a Stream terminal op and does
///   not exist on Iterable&lt;T&gt;. The correct translation is colls.iterator().hasNext().
///
/// Array Any() bug fixed:
///   array.Any() was incorrectly emitted as array.iterator().hasNext(),
///   but Java arrays don't have .iterator(). The correct translation is
///   Arrays.stream(array).iterator().hasNext().
/// </summary>
public class EnumerableAnyTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Bug fix: Any() with no arguments ───────────────────────────────────────

    [Fact]
    public void Any_NoArgs_EmitsIteratorHasNext()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public bool IEnumerableAny(IEnumerable<int> colls)
                {
                    return colls != null && colls.Any();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("iterator().hasNext()", result.GeneratedCode);
        Assert.DoesNotContain("anyMatch()", result.GeneratedCode);
    }

    [Fact]
    public void Any_NoArgs_WithNullCheck_PreservesShortCircuit()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public bool Check(IEnumerable<int> colls)
                {
                    return colls != null && colls.Any();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Both the null guard and the iterator check must be present.
        Assert.Contains("colls != null", result.GeneratedCode);
        Assert.Contains("iterator().hasNext()", result.GeneratedCode);
    }

    // ── Bug fix: Any() on arrays ────────────────────────────────────────────────

    [Fact]
    public void Any_NoArgs_OnArray_EmitsArraysStreamIteratorHasNext()
    {
        const string code = """
            using System.Linq;
            class C
            {
                public bool ArrayAny(int[] arr)
                {
                    return arr.Any();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Arrays don't have .iterator() - must use Arrays.stream().iterator().hasNext()
        Assert.Contains("Arrays.stream(arr).iterator().hasNext()", result.GeneratedCode);
        Assert.DoesNotContain("arr.iterator()", result.GeneratedCode);
    }

    [Fact]
    public void Any_NoArgs_OnMethodReturningArray_EmitsArraysStreamIteratorHasNext()
    {
        const string code = """
            using System.Linq;
            class Point
            {
                public int X { get; set; }
                public int Y { get; set; }
            }
            class C
            {
                private Point[] GetPoints() => new Point[] { new Point { X = 1, Y = 2 } };

                public bool HasPoints()
                {
                    return GetPoints().Any();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Method returning array must also use Arrays.stream()
        Assert.Contains("Arrays.stream(getPoints()).iterator().hasNext()", result.GeneratedCode);
    }

    // ── LINQ operations on arrays ────────────────────────────────────────────────

    [Fact]
    public void Select_OnArray_EmitsArraysStream()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class Point
            {
                public int X { get; set; }
                public int Y { get; set; }
                public Point(int x, int y) { X = x; Y = y; }
            }
            class C
            {
                public Point[] GetPoints() => new Point[] { new Point(1, 1) };

                public IList<Point> Transform()
                {
                    return GetPoints().Select(p => new Point(p.X, p.Y)).ToList();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Arrays must use Arrays.stream() for LINQ operations
        Assert.Contains("Arrays.stream(getPoints())", result.GeneratedCode);
    }

    [Fact]
    public void Where_OnArrayVariable_EmitsArraysStream()
    {
        const string code = """
            using System.Linq;
            class C
            {
                public void Test()
                {
                    int[] arr = new int[] { 1, 2, 3 };
                    var result = arr.Where(x => x > 0).ToList();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Array variables must also use Arrays.stream()
        Assert.Contains("Arrays.stream(arr)", result.GeneratedCode);
    }
}
