using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue #10: 有的情况数组不应该转集合
/// (In some cases, arrays should NOT be converted to collections)
///
/// Root cause: The StreamLocalVariables set does not track variable scope — a variable name
/// added to the set in one block persists for the entire method. When the same variable name is
/// reused in a sibling scope with an array type (Point[]), the foreach was incorrectly wrapping
/// the array with .collect(Collectors.toCollection(() -> new ArrayList<>())), producing invalid Java.
///
/// Specifically:
///   1. In one block, `var pts = source.AsQueryable().Where(...)` → pts added to StreamLocalVariables
///      (IQueryable is not in the semanticTypeIsEnumerableLike check, so the var block doesn't collect it)
///   2. In a sibling block, `Point[] pts = arr` → new pts, but StreamLocalVariables still contains "pts"
///   3. `foreach (var p in pts)` → StreamLocalVariables.Contains("pts") = true
///      → exprIsCollectionLike was false (IArrayTypeSymbol is not INamedTypeSymbol)
///      → isStream = true → WRONG: pts.collect(...) generated
///
/// Fix: In TransformForEachStatement, include IArrayTypeSymbol in exprIsCollectionLike check.
///      Arrays are directly iterable in Java and must never be treated as streams.
///      Also: In TransformLocalDeclaration, do not add array-typed variables to StreamLocalVariables.
/// </summary>
public class ArrayForeachNotCollectedTests
{
    /// <summary>
    /// Basic case: Point[] variable declared and iterated with foreach.
    /// Should produce `for (Point p : points)` NOT `for (Point p : points.collect(...))`.
    /// </summary>
    [Fact]
    public void ArrayVariable_Foreach_ShouldIterateDirectly()
    {
        var result = Convert(@"
class Point { public int X, Y; }
class Sample
{
    Point[] getPoints() { return new Point[0]; }
    void Test()
    {
        Point[] points = getPoints();
        foreach (var p in points) { }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("for (Point p : points)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Core bug scenario (issue #10): variable name reuse in sibling scopes within the same method.
    /// First scope: var pts = stream expression (IQueryable, not collected by var block → added to StreamLocalVariables)
    /// Second scope: Point[] pts = arr (array reusing same name)
    /// foreach over the second pts should NOT collect it — arrays are directly iterable in Java.
    /// </summary>
    [Fact]
    public void ArrayVariable_SameNameAsStreamInSiblingScope_ForeachShouldNotCollect()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Point { public int X, Y; }
class Sample
{
    void Test(IEnumerable<Point> source, Point[] arr)
    {
        {
            var pts = source.AsQueryable().Where(p => p.X > 0);
            // pts is IQueryable - stream-like variable added to StreamLocalVariables
        }
        {
            Point[] pts = arr;
            foreach (var p in pts) { }
        }
    }
}");

        Assert.True(result.Success);
        // The foreach over the array should iterate directly, NOT collect
        Assert.Contains("for (Point p : pts)", result.GeneratedCode, StringComparison.Ordinal);
        // Must NOT wrap the array pts in .collect(...)
        Assert.DoesNotContain("pts.collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Array parameter iterated with foreach should NOT be collected.
    /// </summary>
    [Fact]
    public void ArrayParameter_Foreach_ShouldIterateDirectly()
    {
        var result = Convert(@"
class Point { public int X, Y; }
class Sample
{
    void Test(Point[] points)
    {
        foreach (var p in points) { }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("for (Point p : points)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Array field iterated with foreach should NOT be collected.
    /// </summary>
    [Fact]
    public void ArrayField_Foreach_ShouldIterateDirectly()
    {
        var result = Convert(@"
class Point { public int X, Y; }
class Sample
{
    Point[] points;
    void Test()
    {
        foreach (var p in points) { }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("for (Point p : points)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Array from ToArray() LINQ chain iterated with foreach should NOT be collected.
    /// </summary>
    [Fact]
    public void ArrayFromToArray_Foreach_ShouldIterateDirectly()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Point { public int X, Y; }
class Sample
{
    void Test(IEnumerable<Point> source)
    {
        Point[] points = source.Where(p => p.X > 0).ToArray();
        foreach (var p in points) { }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("for (Point p : points)", result.GeneratedCode, StringComparison.Ordinal);
        // The array result should not get double-collected
        Assert.DoesNotContain("points.collect(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sibling-scope reuse: first block uses a stream variable, second block uses
    /// an array with the same variable name. Regression test for the specific
    /// pattern in the issue report.
    /// </summary>
    [Fact]
    public void StreamVarThenArrayVar_SameName_ForeachCorrect()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Point { public int X, Y; }
class Sample
{
    void Test(IEnumerable<Point> source, Point[] arr)
    {
        // First: 'pts' as stream (Where returns IEnumerable; collected by var block → added to StreamLocalVariables)
        {
            var pts = source.Where(p => p.X > 0);
            foreach (var p in pts) { }
        }
        // Second: 'pts' as array — must iterate directly without collecting
        {
            Point[] pts = arr;
            foreach (var p in pts) { }
        }
    }
}");

        Assert.True(result.Success);
        // Both foreaches should be present; the second (array) one must not collect
        var code = result.GeneratedCode;
        // Array foreach should iterate directly
        int idx = code.LastIndexOf("for (Point p : pts)", StringComparison.Ordinal);
        Assert.True(idx >= 0, "Should have 'for (Point p : pts)' for the array foreach");
        // After the last for-each, there should be no .collect() call immediately following "pts"
        Assert.DoesNotContain("pts.collect(", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
