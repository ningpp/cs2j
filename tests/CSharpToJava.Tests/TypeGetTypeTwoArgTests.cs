using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TypeGetTypeTwoArgTests
{
    [Fact]
    public void TypeGetType_WithThrowOnError_MapsToTypeHelper()
    {
        var result = Convert(@"
public class Sample
{
    public static System.Type GetTypeIfExists(string name)
    {
        return System.Type.GetType(name, false);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("TypeHelper.getType(name, false)", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeHelper", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeGetType_SingleArg_StaysClassForName()
    {
        var result = Convert(@"
public class Sample
{
    public static System.Type GetType(string name)
    {
        return System.Type.GetType(name);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Class.forName(name)", code, StringComparison.Ordinal);
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
