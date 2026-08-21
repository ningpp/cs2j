using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# tuple expressions with int literals in positions that
/// require double (e.g. (int, int, int, int, bool, double)) properly
/// convert the int literal to a double in the generated Java Tuple.of() call.
///
/// In C#, int→double is an implicit conversion. In Java, Tuple.of() infers
/// the type argument from the literal, so an int literal produces Integer
/// instead of Double, causing "incompatible bounds" type inference errors.
/// </summary>
public class TupleIntToDoubleTests
{
    private readonly ITestOutputHelper _out;

    public TupleIntToDoubleTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TupleExpression_IntLiteralInDoublePosition_ConvertsToDouble()
    {
        // Mirrors E:\agl-master\GraphLayout\...\LayeredTestSupport.cs:
        //   var edges = new List<(int S, int T, int W, int Sep, bool Label, double Len)>();
        //   edges.Add((i, i + 1, 1, 1, false, 1));
        // The last "1" is a double in the tuple type, but an int literal in C#.
        var result = Convert("""
using System;
using System.Collections.Generic;

class Test
{
    static void Run()
    {
        var edges = new List<(int, int, int, int, bool, double)>();
        int i = 0;
        edges.Add((i, i + 1, 1, 1, false, 1));
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The last "1" in the tuple should be converted to a double literal (1.0)
        // or boxed as Double.valueOf(1) to satisfy Tuple6<...,Double> inference.
        Assert.Contains("Tuple.of(", code, StringComparison.Ordinal);
        // The int-to-double conversion should be present in the last position
        Assert.DoesNotContain("Tuple.of(i, i + 1, 1, 1, false, 1)", code, StringComparison.Ordinal);
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
