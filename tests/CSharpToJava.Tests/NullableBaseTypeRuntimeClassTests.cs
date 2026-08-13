using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NullableBaseTypeRuntimeClassTests
{
    [Fact]
    public void BaseTypeWithNullableTypeArg_UsesRuntimeClassParameterInSuper()
    {
        var result = Convert(@"
public class JsonConverter<T>
{
    protected JsonConverter()
    {
        System.Type t = typeof(T);
    }
}

public class NullableConverter<T> : JsonConverter<T?> where T : struct
{
    public NullableConverter()
    {
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("super(tClass)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("super(T.class)", code, StringComparison.Ordinal);
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
