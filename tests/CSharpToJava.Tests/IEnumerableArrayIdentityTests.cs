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
    public void ArrayAssignedToIEnumerableLocal_UsesIdentityPreservingView()
    {
        var result = Convert("""
using System.Collections.Generic;

struct Point { public double X, Y; }

class User
{
    void M(Point[] arr)
    {
        IEnumerable<Point> pts = arr;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ArrayHelper.asIterable(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ArrayHelper.toList(arr)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayArgumentToIEnumerableParameter_UsesIdentityPreservingView()
    {
        var result = Convert("""
using System.Collections.Generic;

struct Point { public double X, Y; }

class User
{
    void M(Point[] arr)
    {
        Consume(arr);
    }

    void Consume(IEnumerable<Point> p) { }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("consume(ArrayHelper.asIterable(arr))", result.GeneratedCode, StringComparison.Ordinal);
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
    public void LocalIEnumerableDeclaredFromToArrayCall_UsesIdentityPreservingView()
    {
        // ConstrainedOrdering.CreateInitialOrderInLayers:
        //   IEnumerable<IntPair> liftedLeftRightRelations = LiftLeftRightRelationsToComponentRoots(...).ToArray();
        // The RHS stream-toArray result is a raw Java array; storing it in the
        // IEnumerable slot must still use the identity-preserving asIterable view.
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;

struct Point { public double X, Y; }

class Sample
{
    void M(IEnumerable<Point> src)
    {
        IEnumerable<Point> pts = src.ToArray();
        Consume(pts);
    }

    void Consume(IEnumerable<Point> p) { }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The declaration must wrap the .ToArray() result (a raw Java array)
        // with the identity-preserving view, not pass it through unwrapped.
        Assert.Contains("ArrayHelper.asIterable(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Consume(StreamSupport.stream(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("var pts = StreamSupport.stream(", result.GeneratedCode, StringComparison.Ordinal);
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

    [Fact]
    public void GenericMethodArgumentFromToArrayDeclaredLocal_IsIterable()
    {
        // ConstrainedOrdering: TopologicalSort.GetOrderOnEdges<TEdge>(IEnumerable<TEdge>)
        // called with a local declared as IEnumerable<IntPair> initialized from .ToArray().
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;

struct IntPair { public int A, B; }

interface IEdge { }

class TopologicalSort
{
    public static int[] GetOrderOnEdges<TEdge>(IEnumerable<TEdge> edges) where TEdge : IEdge
    {
        return new int[0];
    }
}

class Sample
{
    void M(IEnumerable<IntPair> src)
    {
        IEnumerable<IntPair> lifted = src.ToArray();
        int[] order = TopologicalSort.GetOrderOnEdges(lifted);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The local must be wrapped once at the declaration, so the generic call
        // receives an Iterable rather than a raw IntPair[].
        Assert.Contains("ArrayHelper.asIterable(", result.GeneratedCode, StringComparison.Ordinal);
        // The call site itself needs no array wrap: the local is already Iterable.
        Assert.Contains("getOrderOnEdges(lifted)", result.GeneratedCode, StringComparison.Ordinal);
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
