using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class IsPrimitivePatternBoxingTests
{
    [Fact]
    public void IsPrimitiveType_UsesBoxedTypeInInstanceOf()
    {
        const string code = """
            public class C {
                bool M(object o) {
                    if (o is int) {
                        return true;
                    }
                    return false;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("instanceof Integer", result.GeneratedCode);
        Assert.DoesNotContain("instanceof int", result.GeneratedCode);
    }
}
