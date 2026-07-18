using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that .NET exception APIs without direct Java equivalents are rewritten
/// to the <see cref="ExceptionCompat"/> helper class.
/// </summary>
public class ExceptionCompatTests
{
    [Fact]
    public void Exception_InnerExceptionAssignment_UsesCompatHelper()
    {
        var result = Convert(@"
class Sample
{
    void M(System.Exception e)
    {
        if (e.InnerException != null)
            e = e.InnerException;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        System.IO.File.WriteAllText("d:\\code\\cs2j\\gen1.txt", result.GeneratedCode);
        Assert.Contains("ExceptionCompat.getInnerException(e)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".getCause()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_SourceAccess_UsesCompatHelper()
    {
        var result = Convert(@"
class Sample
{
    String M(System.Exception e) => e.Source;
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        System.IO.File.WriteAllText("d:\\code\\cs2j\\gen2.txt", result.GeneratedCode);
        Assert.Contains("ExceptionCompat.getSource(e)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_GetInnerExceptionMethod_UsesCompatHelper()
    {
        var result = Convert(@"
class Sample
{
    void M(System.Exception e)
    {
        var inner = e.GetInnerException();
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ExceptionCompat.getInnerException(e)", result.GeneratedCode, StringComparison.Ordinal);
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
