using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NewConstraintTypeParameterTests
{
    [Fact]
    public void NewT_WithNewConstraint_UsesActivatorWithRuntimeClass()
    {
        var result = Convert(@"
public class ReferenceResolver
{
    public ReferenceResolver()
    {
    }
}

public class ReferenceHandler<T> where T : ReferenceResolver, new()
{
    public ReferenceResolver CreateResolver()
    {
        return new T();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(T) io.github.ningpp.compat.Activator.createInstance(tClass)", code, StringComparison.Ordinal);
        Assert.Contains("private Class<?> tClass", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return new Object()", code, StringComparison.Ordinal);
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
