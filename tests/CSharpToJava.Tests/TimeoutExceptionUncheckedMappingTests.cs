using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# System.TimeoutException maps to the compat UNCHECKED
/// TimeoutException (io.github.ningpp.compat.TimeoutException extends
/// SystemException extends RuntimeException), NOT java.util.concurrent.TimeoutException
/// which is a checked exception. Reproduces RegexRunner.java:
/// "未报告的异常错误 RegexMatchTimeoutException; 必须对其进行捕获或声明以便抛出" —
/// the converted RegexMatchTimeoutException extended the checked
/// java.util.concurrent.TimeoutException, so throwing it required a throws clause.
/// C# exceptions are all unchecked, so the compat base must be unchecked too.
/// </summary>
public class TimeoutExceptionUncheckedMappingTests
{
    [Fact]
    public void TimeoutExceptionBase_MapsToCompatUnchecked()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
public class MyTimeout : System.TimeoutException
{
    public MyTimeout(string message) : base(message) { }
}",
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("extends TimeoutException", code);
        Assert.Contains("io.github.ningpp.compat.TimeoutException", code);
        Assert.DoesNotContain("java.util.concurrent.TimeoutException", code);
    }

    [Fact]
    public void TimeoutExceptionMappingEntry_PointsToCompatUnchecked()
    {
        var registry = new CSharpToJava.TypeMapping.TypeMappingRegistry();

        Assert.Equal("TimeoutException", registry.MapType("System.TimeoutException"));
        Assert.Contains("io.github.ningpp.compat.TimeoutException", registry.GetRequiredImports("System.TimeoutException"));
        Assert.DoesNotContain("java.util.concurrent.TimeoutException", registry.GetRequiredImports("System.TimeoutException"));
    }
}
