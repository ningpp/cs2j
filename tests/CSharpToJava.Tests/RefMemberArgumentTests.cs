using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class RefMemberArgumentTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void RefMemberArgument_UsesHolderAndWritesBack()
    {
        const string code = """
            class Box { public int Value; }
            class C
            {
                static void Inc(ref int x) { x++; }
                void M(Box b)
                {
                    Inc(ref b.Value);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("IntHolder _refArgHolder", result.GeneratedCode);
        Assert.Contains("inc(_refArgHolder", result.GeneratedCode);
    }
}
