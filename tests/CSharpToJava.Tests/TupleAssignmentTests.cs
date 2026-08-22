using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TupleAssignmentTests
{
    [Fact]
    public void TupleAssignToAutoProperties_SplitsIntoBackingFieldWrites()
    {
        var result = Convert(@"
public struct Point2D
{
    public int X { get; }

    public int Y { get; }

    public Point2D(int x, int y) => (X, Y) = (x, y);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("this.x = x;", code, StringComparison.Ordinal);
        Assert.Contains("this.y = y;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuple.of", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleAssignStatement_WithMembersAndConstants_SplitsPerElement()
    {
        var result = Convert(@"
public class Point3D
{
    public int X { get; }

    public int Y { get; }

    public int Z { get; }

    public Point3D(int x, int y)
    {
        (X, Y, Z) = (x, y, 0);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("this.x = x;", code, StringComparison.Ordinal);
        Assert.Contains("this.y = y;", code, StringComparison.Ordinal);
        Assert.Contains("this.z = 0;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleSwapWithOverlappingElements_HoistsTemps()
    {
        var result = Convert(@"
public class Swapper
{
    public void Swap(int[] array, int start, int i)
    {
        (array[start], array[i]) = (array[i], array[start]);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("var _cs2jTupleTmp0 = array[i];", code, StringComparison.Ordinal);
        Assert.Contains("var _cs2jTupleTmp1 = array[start];", code, StringComparison.Ordinal);
        Assert.Contains("array[start] = _cs2jTupleTmp0;", code, StringComparison.Ordinal);
        Assert.Contains("array[i] = _cs2jTupleTmp1;", code, StringComparison.Ordinal);
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
