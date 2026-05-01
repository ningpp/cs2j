using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests.Java2;

public class PipelineComparisonTests
{
    [Fact]
    public void NewPipeline_EmptyClass_ProducesValidJava()
    {
        var options = new ConversionOptions();
        var pipeline = new NewConversionPipeline();
        var result = pipeline.Convert("class Empty { }", options);

        Assert.True(result.Success, "Pipeline failed: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.NotEmpty(result.GeneratedCode);
        Assert.Contains("class Empty", result.GeneratedCode);
    }

    [Fact]
    public void NewPipeline_ClassWithMethod_ProducesValidJava()
    {
        var options = new ConversionOptions();
        var pipeline = new NewConversionPipeline();
        var result = pipeline.Convert("class Calc { int Add(int a, int b) { return a + b; } }", options);

        Assert.True(result.Success);
        Assert.Contains("class Calc", result.GeneratedCode);
        Assert.Contains("int Add", result.GeneratedCode);
    }

    [Fact]
    public void NewPipeline_DoesNotContainRawCSharpTokens()
    {
        var options = new ConversionOptions();
        var pipeline = new NewConversionPipeline();
        var result = pipeline.Convert("class Foo { void Bar() { int x = 1; } }", options);

        Assert.True(result.Success);
        Assert.DoesNotContain("var ", result.GeneratedCode);
        Assert.DoesNotContain("using ", result.GeneratedCode);
    }
}
