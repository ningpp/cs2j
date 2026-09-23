using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ReflectionGetMethodMappingTests
{

    [Fact]
    public void TypeOf_Assembly_GetName_Version_UsesAssemblyCompat()
    {
        var result = Convert(@"
public class SvgGraphWriter
{
    public string VersionComment()
    {
        return ""SvgWriter version "" + typeof(SvgGraphWriter).Assembly.GetName().Version;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("import io.github.ningpp.compat.TypeHelper", code, StringComparison.Ordinal);
        Assert.Contains("TypeHelper.getAssembly(SvgGraphWriter.class)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SvgGraphWriter.class.getPackage().getPackage()", code, StringComparison.Ordinal);
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
