using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NestedTypeArityConflictTests
{
    [Fact]
    public void SameNameDifferentArity_RenamesGenericNestedType()
    {
        var result = Convert(@"
public class Outer
{
    private class ListConverter : object
    {
    }

    private class ListConverter<T> : object
    {
    }

    public static ListConverter<int> Make()
    {
        return new ListConverter<int>();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("class ListConverter_1tp<T>", code, StringComparison.Ordinal);
        Assert.Contains("ListConverter_1tp<Integer>", code, StringComparison.Ordinal);
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
