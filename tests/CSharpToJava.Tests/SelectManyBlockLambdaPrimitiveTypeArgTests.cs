using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that when a SelectMany lambda's parameter type is a C# primitive
/// (e.g. int), the Java output uses the boxed wrapper type (Integer) as
/// the generic type argument in Function&lt;T, ?&gt; casts, not the primitive
/// keyword (int) which is invalid in Java generics.
///
/// This mirrors the real XCoordsRoutingV2Test.XCoordinates_Grid_NoOverlaps_Test
/// scenario where:
///   Enumerable.Range(0, 6).SelectMany(r => Enumerable.Range(0, 6)
///       .SelectMany(c => { int id = r * 6 + c; ... }))
/// The block lambda's parameter 'c' has type int, and the converter wraps
/// the block lambda as ((Function&lt;int, Iterable&lt;?&gt;&gt;) (c -> { ... }))
/// which fails to compile because Java generics don't accept primitives.
/// </summary>
public class SelectManyBlockLambdaPrimitiveTypeArgTests
{
    private readonly ITestOutputHelper _out;

    public SelectManyBlockLambdaPrimitiveTypeArgTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void SelectMany_BlockLambda_IntParam_UsesIntegerNotIntInFunctionCast()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;
using System.Linq;

class Test
{
    static void Run()
    {
        var result = Enumerable.Range(0, 3)
            .SelectMany(r => Enumerable.Range(0, 3)
                .SelectMany(c =>
                {
                    int id = r * 3 + c;
                    var edges = new List<(int, int)>();
                    if (c + 1 < 3)
                        edges.Add((id, id + 1));
                    return edges;
                }))
            .ToList();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The Function cast must NOT use primitive "int" as type argument.
        Assert.DoesNotContain("Function<int,", code, StringComparison.Ordinal);

        // It should use Integer (the boxed wrapper) instead.
        Assert.Contains("Function<Integer", code, StringComparison.Ordinal);
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
