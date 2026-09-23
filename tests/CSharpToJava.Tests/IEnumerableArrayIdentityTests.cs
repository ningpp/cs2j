using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# stores the array instance itself when an array is assigned to an
/// IEnumerable&lt;T&gt; target, and a later (T[]) cast returns the SAME instance.
/// Code such as StaircaseRemover relies on that identity:
/// SegWithIndex.Equals compares the backing Point[] by reference, so a
/// staircase is only removable when the RTree segments and the current points
/// share one array instance. ArrayHelper.toList copies, breaking the round
/// trip; the identity-preserving ArrayHelper.asIterable view must be used for
/// IEnumerable targets instead.
/// </summary>
public class IEnumerableArrayIdentityTests
{
    [Fact]
    public void ArrayAssignedToIEnumerableProperty_UsesIdentityPreservingView()
    {
        var result = Convert("""
using System.Collections.Generic;

struct Point { public double X, Y; }

class Path
{
    public IEnumerable<Point> PathPoints { get; set; }
}

class User
{
    void M(Path path, Point[] pts)
    {
        path.PathPoints = pts;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The setter must receive an array-backed view, not a copied list.
        Assert.Contains("setPathPoints(ArrayHelper.asIterable(pts))", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("setPathPoints(ArrayHelper.toList(pts))", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CastBackFromIEnumerableProperty_StillUsesToArray()
    {
        var result = Convert("""
using System.Collections.Generic;

struct Point { public double X, Y; }

class Path
{
    public IEnumerable<Point> PathPoints { get; set; }
}

class User
{
    Point[] M(Path path)
    {
        return (Point[])path.PathPoints;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Cast-back sites materialize via toArray; with asIterable this returns
        // the original array instance (identity-preserving).
        Assert.Contains(".toArray(new Point[0])", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayInitializerForIEnumerableProperty_UsesIdentityPreservingView()
    {
        var result = Convert("""
using System.Collections.Generic;

struct Point { public double X, Y; }

class Holder
{
    public IEnumerable<Point> Points { get; } = new[] { new Point { X = 1, Y = 2 } };
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ArrayHelper.asIterable(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerablePropertyAssignedToArrayFromToArrayCall_UsesIdentityPreservingView()
    {
        // Nudger.ShiftPathEdges / PathRefiner.AdjustPaths:
        //   path.PathPoints = GetShiftedPoints(path).ToArray();
        // The RHS .ToArray() conversion starts with StreamSupport.stream(...) but
        // terminates to a raw Java array, so the property setter must still wrap
        // it with the identity-preserving asIterable view.
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;

struct Point { public double X, Y; }

class Path
{
    public IEnumerable<Point> PathPoints { get; set; }
}

class Sample
{
    void M(Path path, IEnumerable<Point> src)
    {
        path.PathPoints = src.ToArray();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("setPathPoints(ArrayHelper.asIterable(", result.GeneratedCode, StringComparison.Ordinal);
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
