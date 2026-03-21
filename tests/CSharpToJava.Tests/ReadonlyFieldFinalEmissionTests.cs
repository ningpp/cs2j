using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ReadonlyFieldFinalEmissionTests
{
    [Fact]
    public void ReadonlyField_WithDefaultInitialization_DoesNotEmitJavaFinal()
    {
        const string code = """
            public class C {
                readonly int[] _sizes;

                public C() { }

                public C(int[] sizes) {
                    _sizes = sizes;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("final int[] _sizes", result.GeneratedCode);
        Assert.Contains("int[] _sizes", result.GeneratedCode);
    }
}
