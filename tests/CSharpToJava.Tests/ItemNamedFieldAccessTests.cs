using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ItemNamedFieldAccessTests
{
    private static ConversionResult Convert(string source)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = source,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void UserStructFieldNamedItem1_IsNotMappedToTupleAccessor()
    {
        var result = Convert("""
class FourStackStrings {
    public string Item1;
    public string Item2;
    public string Item3;
    public string Item4;
}
class C {
    string Test() {
        FourStackStrings stackStrings = new FourStackStrings();
        stackStrings.Item1 = "a";
        return stackStrings.Item1 + stackStrings.Item4;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("stackStrings.Item1", code);
        Assert.DoesNotContain("stackStrings._1()", code);
    }
}
