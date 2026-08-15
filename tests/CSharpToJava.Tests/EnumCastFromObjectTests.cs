using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for C# unboxing casts from object to integral types.
/// </summary>
public class EnumCastFromObjectTests
{
    [Fact]
    public void CastObjectToInt32_UsesEnumHelper()
    {
        var result = Convert(@"
public class C
{
    public static long F(object value)
    {
        return (long)(int)value;
    }
}");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode;
        // (int)object must route through EnumHelper so boxed enum constants
        // (which are not Java Numbers) can be unboxed to their underlying value.
        Assert.Contains("EnumHelper.toInt32(value)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CastObjectToInt64_UsesEnumHelper()
    {
        var result = Convert(@"
public class C
{
    public static long F(object value)
    {
        return (long)value;
    }
}");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode;
        Assert.Contains("EnumHelper.toInt64(value)", code, StringComparison.Ordinal);
    }

    private static Core.Pipeline.ConversionResult Convert(string sourceCode)
    {
        var pipeline = new Core.Pipeline.ConversionPipeline();
        return pipeline.Convert(new Core.Pipeline.ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new Core.Context.ConversionOptions(),
        });
    }
}
