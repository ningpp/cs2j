using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class OutParameterFallbackRefKindTests
{
    [Fact]
    public void OutArgument_UsesHolderPattern_ForExistingVariableInSameDeclaration()
    {
        const string code = """
            public class C {
                class N {}

                N Split(out N r) {
                    r = new N();
                    return r;
                }

                N M() {
                    N l = new N(), r;
                    l = l.Split(out r);
                    return r;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("ObjectHolder<", result.GeneratedCode);
        Assert.DoesNotContain("split(r)", result.GeneratedCode);
    }
}
