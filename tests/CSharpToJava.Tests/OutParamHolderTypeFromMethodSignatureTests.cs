using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that out parameters to existing (potentially unresolvable) variables
/// produce correct holder types based on the called method's parameter type.
/// </summary>
public class OutParamHolderTypeFromMethodSignatureTests
{
    [Fact]
    public void OutDouble_UnresolvedVar_UsesDoubleHolder()
    {
        // Simulates the pattern from LINQ-rewriter extracted methods where the
        // 'out t' variable was declared in an outer scope and is now undeclared.
        // The converter should resolve the holder type from the method parameter type.
        var source = @"
using System;

class Point {
    public static double DistToLineSegment(Point p, Point p0, Point p1, out double t) {
        t = 0.5;
        return 0.0;
    }
}

class Sample {
    void M(Point[] pts, Point p0, Point p1) {
        double t;
        foreach (var p in pts) {
            if (Point.DistToLineSegment(p, p0, p1, out t) < 1e-5) {
                Console.WriteLine(t);
            }
        }
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // Should use DoubleHolder, not ObjectHolder<Object>
        Assert.Contains("DoubleHolder", result.GeneratedCode);
        Assert.DoesNotContain("ObjectHolder", result.GeneratedCode);
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
