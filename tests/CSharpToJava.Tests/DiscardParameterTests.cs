using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for C# discard parameter ('_') handling in Java conversion.
/// Java 9+ does not allow '_' as an identifier, so it must be escaped.
/// </summary>
public class DiscardParameterTests
{
    [Fact]
    public void DiscardParameterInConstructor_EscapedInJava()
    {
        var result = Convert(@"
public class Sample
{
    public Sample(object _)
    {
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Java does not allow '_' as parameter name
        Assert.DoesNotContain("(object _)", result.GeneratedCode);
    }

    [Fact]
    public void DiscardParameterInMethod_EscapedInJava()
    {
        var result = Convert(@"
public class Sample
{
    public void M(object _)
    {
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Java does not allow '_' as parameter name
        Assert.DoesNotContain("(object _)", result.GeneratedCode);
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
