using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class WeakReferenceTypeMappingTests
{
    [Fact]
    public void WeakReference_New_And_IsAlive_And_Target_AllMapped()
    {
        var result = Convert(@"
using System;
class Sample
{
    object M()
    {
        var wr = new WeakReference(new object());
        if (wr.IsAlive)
            return wr.Target;
        return null;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("io.github.ningpp.compat.WeakReference", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.WeakReference", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
