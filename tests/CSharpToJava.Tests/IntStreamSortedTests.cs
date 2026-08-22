using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ OrderBy on primitive arrays (int[], long[], double[])
/// uses no-arg sorted() instead of sorted(Comparator) because Java's
/// IntStream/LongStream/DoubleStream.sorted() doesn't accept a Comparator.
/// </summary>
public class IntStreamSortedTests
{
    private readonly ITestOutputHelper _out;

    public IntStreamSortedTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void OrderBy_OnIntArray_UsesNoArgSorted()
    {
        var result = Convert("""
using System.Linq;

class Test
{
    static string Run(int[] indices)
    {
        var sorted = indices.OrderBy(i => i).ToArray();
        return string.Join(",", sorted);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // Should use no-arg sorted(), not sorted(Comparator...)
        Assert.Contains(".sorted()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("sorted(Comparator", code, StringComparison.Ordinal);
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
