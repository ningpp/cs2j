using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class LogicalNotOnIntIrTests
{
    [Fact]
    public void LogicalNot_OnInt_EmitsEqualToZero()
    {
        var result = Convert(@"
class Test
{
    bool M(int x)
    {
        if (!x)
            return false;
        return true;
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain("!x", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(x == 0)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LogicalNot_OnBool_KeepsExclamation()
    {
        var result = Convert(@"
class Test
{
    bool M(bool flag)
    {
        return !flag;
    }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("!flag", result.GeneratedCode, StringComparison.Ordinal);
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
