using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Text.StringBuilder maps to java.lang.StringBuilder, whose length property
/// is length() — the Length property must be mapped, not emitted as getLength().
/// Regression: RegexCharClass uses _categories?.Length ?? 0 where _categories is a
/// StringBuilder.
/// </summary>
public class StringBuilderLengthMappingTests
{
    [Fact]
    public void StringBuilderLengthProperty_MapsToLengthMethod()
    {
        var result = Convert(@"
using System.Text;
class C
{
    private StringBuilder _categories;

    int ComputeLength()
    {
        return _categories?.Length ?? 0;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("_categories.length()", code);
        Assert.DoesNotContain("getLength()", code);
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
