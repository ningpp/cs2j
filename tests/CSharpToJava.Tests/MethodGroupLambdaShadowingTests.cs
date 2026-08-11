using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that lambdas synthesized from method groups rename their parameters when
/// the default names (sender/e) collide with names visible in the enclosing scope
/// (e.g. a method parameter named e). Java forbids lambda parameter shadowing.
/// </summary>
public class MethodGroupLambdaShadowingTests
{
    [Fact]
    public void InstanceHandler_MethodParamNamedE_RenamesLambdaParam()
    {
        var result = Convert(@"
using System;
class T {
    void Handler(object sender, EventArgs e) { }
    void M(EventArgs e) {
        EventHandler h = Handler;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("(sender, _e) -> handler(sender, _e)", result.GeneratedCode);
        Assert.DoesNotContain("(sender, e) ->", result.GeneratedCode);
    }

    [Fact]
    public void StaticHandler_MethodParamNamedE_RenamesLambdaParam()
    {
        var result = Convert(@"
using System;
class T {
    static void Handler(object sender, EventArgs e) { }
    void M(EventArgs e) {
        EventHandler h = Handler;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("(sender, _e) -> T.handler(sender, _e)", result.GeneratedCode);
    }

    [Fact]
    public void Handler_MethodParamNamedSender_RenamesLambdaSender()
    {
        var result = Convert(@"
using System;
class T {
    void Handler(object sender, EventArgs e) { }
    void M(object sender) {
        EventHandler h = Handler;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("(_sender, e) -> handler(_sender, e)", result.GeneratedCode);
    }

    [Fact]
    public void MemberAccessHandler_MethodParamNamedE_RenamesLambdaParam()
    {
        var result = Convert(@"
using System;
class T {
    void Handler(object sender, EventArgs e) { }
    void M(EventArgs e) {
        EventHandler h = this.Handler;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("(sender, _e) ->", result.GeneratedCode);
        Assert.DoesNotContain("(sender, e) ->", result.GeneratedCode);
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
