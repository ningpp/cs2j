using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StringFallbackApiTests
{
    private static ConversionResult Convert(string code)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = code });
    }

    [Fact]
    public void StringIsNullOrEmpty_MapsToNullOrEmptyCheck()
    {
        const string code = """
            using System;

            class C
            {
                bool M(string s)
                {
                    return String.IsNullOrEmpty(s);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("(s == null || s.isEmpty())", result.GeneratedCode);
        Assert.DoesNotContain("String.IsNullOrEmpty", result.GeneratedCode);
    }

    [Fact]
    public void StringEmpty_MapsToEmptyLiteral()
    {
        const string code = """
            using System;

            class C
            {
                string M()
                {
                    return String.Empty;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return \"\";", result.GeneratedCode);
        Assert.DoesNotContain("String.Empty", result.GeneratedCode);
    }
}
