using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# byte.Parse maps to Byte.parseByte in Java (Byte, not Integer, owns parseByte).
/// </summary>
public class PrimitiveStaticParseMappingTests
{
    [Fact]
    public void ByteParse_StaticInvocation_UsesByteWrapper()
    {
        var result = Convert(@"
using System;
class T {
    void M(string s) {
        var r = byte.Parse(s);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Byte.parseByte(s)", result.GeneratedCode);
        Assert.DoesNotContain("Integer.parseByte", result.GeneratedCode);
    }

    [Fact]
    public void SByteParse_StaticInvocation_UsesByteWrapper()
    {
        var result = Convert(@"
using System;
class T {
    void M(string s) {
        var r = sbyte.Parse(s);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Byte.parseByte(s)", result.GeneratedCode);
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
