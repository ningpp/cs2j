using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that out parameter holders in LINQ-rewriter extracted methods
/// use correctly typed holders (DoubleHolder, IntHolder, etc.) and
/// properly declare the read-back variable when the original variable
/// is not in scope.
/// </summary>
public class OutParamHolderTypeFallbackTests
{
    private readonly ITestOutputHelper _out;
    public OutParamHolderTypeFallbackTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions { PreferStreamApi = false },
        });
    }

    [Fact]
    public void OutDouble_InLinqWhere_UsesDoubleHolderOrTypedDecl()
    {
        var r = Convert(@"
using System;
using System.Linq;
class Point {
    public double X, Y;
    public static double DistToSeg(Point p, Point a, Point b, out double t) {
        t = 0.5;
        return Math.Sqrt(p.X * p.X + p.Y * p.Y);
    }
}
class Sample {
    Point[] Filter(Point[] pts, Point a, Point b) {
        double t;
        return pts.Where(p => Point.DistToSeg(p, a, b, out t) < 1e-5).ToArray();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should NOT use ObjectHolder<Object> — should use DoubleHolder
        Assert.DoesNotContain("ObjectHolder", code);
        // The read-back variable should be properly typed (not 'var' without declaration)
        Assert.DoesNotContain("var t =", code);
    }
}
