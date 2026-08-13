using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DelegateConstraintMappingTests
{
    [Fact]
    public void DelegateConstraint_IsDroppedFromJavaTypeParameterBounds()
    {
        var result = Convert(@"
using System;

public class Sample
{
    private static T CreateDelegate<T>(dynamic method) where T : Delegate
    {
        return default;
    }

    public static Action<object, int> M()
    {
        return CreateDelegate<Action<object, int>>(null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("<T>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("T extends Delegate", code, StringComparison.Ordinal);
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
