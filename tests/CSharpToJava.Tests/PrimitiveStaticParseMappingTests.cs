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

    [Fact]
    public void UIntTryParse_NullProvider_EmitsDisambiguatedCultureInfoNull()
    {
        var result = Convert(@"
using System;
using System.Globalization;
class T {
    int M(string name) {
        return uint.TryParse(name, NumberStyles.None, provider: null, out uint result) && result < 10 ? (int)result : -1;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("tryParseUInt(name, NumberStyles.None, (CultureInfo) null,", result.GeneratedCode);
        Assert.DoesNotContain("tryParseUInt(name, NumberStyles.None, null,", result.GeneratedCode);
    }

    [Fact]
    public void DoubleParse_NullProvider_EmitsDisambiguatedCultureInfoNull()
    {
        var result = Convert(@"
using System;
using System.Globalization;
class T {
    double M(string s) {
        return double.Parse(s, NumberStyles.Float, provider: null);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("parseDouble(s, NumberStyles.Float, (CultureInfo) null)", result.GeneratedCode);
        Assert.DoesNotContain("parseDouble(s, NumberStyles.Float, null)", result.GeneratedCode);
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
