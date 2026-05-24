using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ref holder initialization correctly uses the current value
/// when the same variable was previously assigned via an out parameter.
/// </summary>
public class RefOutSameVariableTests
{
    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    /// <summary>
    /// When a local variable is used first as an out parameter (assigned by callee)
    /// and then as a ref parameter (read+write), the ref holder must be initialized
    /// with the variable's current value — not with an empty default.
    ///
    /// C#: double par0, par1; Point x;
    ///     CrossTwoLineSegs(..., out par0, out par1, out x);
    ///     AdjustSolution(..., ref par0, ref par1, ref x);
    ///
    /// The ref holders should be:
    ///     new DoubleHolder(par0) — not new DoubleHolder()
    ///     new ObjectHolder<>(x) — not new ObjectHolder<>()
    /// </summary>
    [Fact]
    public void OutThenRef_SameVariable_RefHolderShouldGetCurrentValue()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Point {
    public double X, Y;
    public static Point operator +(Point a, Point b) => new Point { X = a.X + b.X, Y = a.Y + b.Y };
    public static Point operator *(double s, Point p) => new Point { X = s * p.X, Y = s * p.Y };
    public static Point operator -(Point a, Point b) => new Point { X = a.X - b.X, Y = a.Y - b.Y };
    public static double operator *(Point a, Point b) => a.X * b.X + a.Y * b.Y;
}

class IntersectionInfo {
    public double Par0, Par1;
    public Point P;
    public IntersectionInfo(double a, double b, Point p) { Par0 = a; Par1 = b; P = p; }
}

class LineSegment {
    public Point Start, End;
    public LineSegment(Point s, Point e) { Start = s; End = e; }
}

class PolylinePoint {
    public Point Point;
    public PolylinePoint Next;
}

class Polyline {
    public PolylinePoint StartPoint;
    public Point Start;
    public bool Closed;
}

class Curve
{
    static bool CrossTwoLineSegs(Point a0, Point a1, Point b0, Point b1,
        double t0, double t1, double s0, double s1,
        out double par0, out double par1, out Point x)
    {
        par0 = 0.5;
        par1 = 0.3;
        x = a0 + b0;
        return true;
    }

    static void AdjustSolution(Point aStart, Point aEnd,
        Point bStart, Point bEnd,
        ref double par0, ref double par1, ref Point x)
    {
        par0 = par0 + 1.0;
        par1 = par1 + 1.0;
        x = x + new Point { X = 1, Y = 1 };
    }

    static bool OldIntersection(List<IntersectionInfo> ret, ref Point x)
    {
        return false;
    }

    static List<IntersectionInfo> GetAllIntersectionsOfLineAndPolyline(
        LineSegment lineSeg, Polyline poly)
    {
        var ret = new List<IntersectionInfo>();
        double offset = 0.0;
        double par0, par1;
        Point x;
        PolylinePoint polyPoint = poly.StartPoint;
        for (; polyPoint != null && polyPoint.Next != null; polyPoint = polyPoint.Next) {
            if (CrossTwoLineSegs(lineSeg.Start, lineSeg.End,
                polyPoint.Point, polyPoint.Next.Point, 0, 1, 0, 1,
                out par0, out par1, out x)) {
                AdjustSolution(lineSeg.Start, lineSeg.End,
                    polyPoint.Point, polyPoint.Next.Point,
                    ref par0, ref par1, ref x);
                if (!OldIntersection(ret, ref x))
                    ret.Add(new IntersectionInfo(par0, offset + par1, x));
            }
            offset++;
        }
        return ret;
    }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode ?? "";

        // The OUT holders for par0, par1, x should use parameter-less constructors
        Assert.Contains("new DoubleHolder();", code);
        Assert.Contains("new ObjectHolder<>();", code);

        // The REF holders for par0 and par1 must be initialized with current values
        // e.g. new DoubleHolder(par0) not new DoubleHolder()
        Assert.Contains("new DoubleHolder(par0)", code);
        Assert.Contains("new DoubleHolder(par1)", code);

        // The REF holder for x must be new ObjectHolder<>(x) not new ObjectHolder<>()
        Assert.Contains("new ObjectHolder<>(x)", code);
    }

    /// <summary>
    /// Regression for MSAGL Curve.CrossOverIntervals:
    /// a non-ref method parameter passed to a ref method is already initialized
    /// and must seed the holder with the current parameter value.
    /// </summary>
    [Fact]
    public void RefParameter_FromOrdinaryParameter_InitializesHolderWithCurrentValue()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Curve
{
    static List<string> CrossOverIntervals(List<string> intersections)
    {
        GoDeeper(ref intersections);
        return intersections;
    }

    static void GoDeeper(ref List<string> intersections)
    {
        intersections.Add(""x"");
    }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ObjectHolder<ArrayList<String>> _intersectionsRef = new ObjectHolder<>(intersections);", code);
        Assert.DoesNotContain("ObjectHolder<ArrayList<String>> _intersectionsRef = new ObjectHolder<>();", code);
        Assert.Contains("intersections = _intersectionsRef.value;", code);
    }

    /// <summary>
    /// C# ref arguments are always read/write; fields have a current value too
    /// even when there is no local definite-assignment fact to prove.
    /// </summary>
    [Fact]
    public void RefParameter_FromField_InitializesHolderWithCurrentValue()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Curve
{
    private List<string> intersections = new List<string>();

    void Cross()
    {
        GoDeeper(ref intersections);
    }

    static void GoDeeper(ref List<string> intersections)
    {
        intersections.Add(""x"");
    }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ObjectHolder<ArrayList<String>> _intersectionsRef = new ObjectHolder<>(intersections);", code);
        Assert.DoesNotContain("ObjectHolder<ArrayList<String>> _intersectionsRef = new ObjectHolder<>();", code);
        Assert.Contains("intersections = _intersectionsRef.value;", code);
    }

    [Fact]
    public void RefParameter_AfterOutAssignmentInSameBlock_InitializesHolderWithCurrentValue()
    {
        var result = Convert(@"
class C
{
    static void Assign(out double value)
    {
        value = 1.0;
    }

    static void Mutate(ref double value)
    {
        value = value + 1.0;
    }

    void Run()
    {
        double value;
        Assign(out value);
        Mutate(ref value);
    }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode ?? "";

        Assert.Contains("DoubleHolder _valueRef = new DoubleHolder(value);", code);
        Assert.DoesNotContain("DoubleHolder _valueRef = new DoubleHolder();", code);
    }
}
