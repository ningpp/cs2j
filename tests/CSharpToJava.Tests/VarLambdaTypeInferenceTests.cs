using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that var declarations with lambda initializers correctly resolve
/// to explicit functional interface types instead of leaving Java's var
/// (which cannot infer lambda target types).
/// </summary>
public class VarLambdaTypeInferenceTests
{
    private readonly ITestOutputHelper _out;

    public VarLambdaTypeInferenceTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void VarWithTwoParamLambda_UsesExplicitBiFunctionType()
    {
        // Mirrors TangentPairTest:
        //   var rect = (Point a, Point b) => new Rectangle(a, b).Perimeter();
        // Java's var cannot infer lambda types, so the converter must emit
        // an explicit BiFunction<Point, Point, Polyline> type.
        var result = Convert("""
using System;

class Point
{
    public double X;
    public double Y;
    public Point(double x, double y) { X = x; Y = y; }
}

class Rectangle
{
    public Rectangle(Point a, Point b) {}
    public int Perimeter() => 42;
}

class Test
{
    static void Run()
    {
        var rect = (Point a, Point b) => new Rectangle(a, b).Perimeter();
        var result = rect(new Point(0, 0), new Point(10, 10));
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The lambda variable must NOT use var — it needs an explicit type.
        Assert.DoesNotContain("var rect =", code, StringComparison.Ordinal);
        // The type should be a BiFunction (2-arg function returning a value).
        Assert.Contains("BiFunction", code, StringComparison.Ordinal);
    }

    [Fact]
    public void VarWithSingleParamLambda_UsesExplicitFunctionType()
    {
        // Mirrors: var poly = (ICurve curve) => Curve.PolylineAroundClosedCurve(curve);
        var result = Convert("""
using System;

interface ICurve { }

class Curve
{
    public static ICurve PolylineAroundClosedCurve(ICurve curve) => curve;
}

class Test
{
    static void Run()
    {
        var poly = (ICurve curve) => Curve.PolylineAroundClosedCurve(curve);
        var result = poly(new Impl());
    }

    class Impl : ICurve { }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The lambda variable must NOT use var — it needs an explicit type.
        Assert.DoesNotContain("var poly =", code, StringComparison.Ordinal);
        // The type should be a Function (1-arg function returning a value).
        Assert.Contains("Function", code, StringComparison.Ordinal);
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
