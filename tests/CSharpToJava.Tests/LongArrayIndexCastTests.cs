using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# long/ulong used as array indices are cast to int in Java output.
/// C# allows long/ulong for array indexing implicitly, but Java requires int.
/// </summary>
public class LongArrayIndexCastTests
{
    private readonly ITestOutputHelper _out;

    public LongArrayIndexCastTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ULongArrayIndex_CastToInt()
    {
        var result = Convert("""
class Test
{
    static void Run()
    {
        var points = new int[10];
        for (ulong i = 0; i < (ulong)10; i++)
        {
            points[i] = (int)i;
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The array index must be cast to int.
        Assert.Contains("(int)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("points[i]", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LongArrayIndex_CastToInt()
    {
        var result = Convert("""
class Test
{
    static void Run()
    {
        var arr = new int[10];
        long idx = 5;
        var val = arr[idx];
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The array index must be cast to int.
        Assert.Contains("(int)", code, StringComparison.Ordinal);
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
