using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for System.Diagnostics.Debug / Trace API conversions.
/// </summary>
public class DebugTraceConversionTests
{
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

    /// <summary>
    /// Debug.WriteLineIf(condition, message) must not be treated as a
    /// String.Format call. The first argument is a boolean condition, not a
    /// format string; treating it as such produces invalid Java.
    /// </summary>
    [Fact]
    public void Debug_WriteLineIf_DoesNotGenerateStringFormatWithBoolean()
    {
        var result = Convert(@"
using System.Diagnostics;
class TestClass
{
    void M(bool flag)
    {
        Debug.WriteLineIf(flag, ""message"");
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Must not produce String.format(boolean, String) which does not compile in Java.
        Assert.DoesNotContain("String.format(flag", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.format(true", result.GeneratedCode, StringComparison.Ordinal);
    }
}
