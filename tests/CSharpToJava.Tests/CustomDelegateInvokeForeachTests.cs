using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that custom delegate Invoke() in foreach loops maps to the
/// correct SAM method (run/accept/apply) instead of being camelCased to invoke().
/// </summary>
public class CustomDelegateInvokeForeachTests
{
    [Fact]
    public void CustomDelegate_InvokeInForeach_MappedToRun()
    {
        var result = Convert(@"
using System.Collections.Generic;

delegate void MyCallback();

class Test
{
    List<MyCallback> _callbacks = new();
    void FireAll()
    {
        foreach (var cb in _callbacks)
            cb.Invoke();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Should be .run() not .invoke()
        Assert.Contains(".run()", result.GeneratedCode);
        Assert.DoesNotContain(".invoke()", result.GeneratedCode);
    }

    [Fact]
    public void CustomDelegate_DirectCallInForeach_MappedToRun()
    {
        var result = Convert(@"
using System.Collections.Generic;

delegate void MyCallback();

class Test
{
    List<MyCallback> _callbacks = new();
    void FireAll()
    {
        foreach (var cb in _callbacks)
            cb();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Should be cb.run() not cb()
        Assert.Contains(".run()", result.GeneratedCode);
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
