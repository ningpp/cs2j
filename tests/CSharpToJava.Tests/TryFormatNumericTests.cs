using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TryFormatNumericTests
{

    [Fact]
    public void NonNumericTryFormat_NotTransformed()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        MyType a = new MyType();
        char[] buf = new char[10];
        a.TryFormat(buf, out int w);
    }
}

public class MyType
{
    public bool TryFormat(char[] buf, out int w) { w = 0; return false; }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("MathHelper.tryFormat", result.GeneratedCode, StringComparison.Ordinal);
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
