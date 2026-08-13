using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class KeywordBackingFieldAssignmentTests
{
    [Fact]
    public void AutoPropertyNamedInt_Assignment_UsesEscapedBackingField()
    {
        var result = Convert(@"
public class ClassWrapper
{
    public int Int { get; }

    public ClassWrapper(int @int)
    {
        Int = @int;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("this.intValue = intValue", code, StringComparison.Ordinal);
        Assert.DoesNotContain("this.int = ", code, StringComparison.Ordinal);
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
