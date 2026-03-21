using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumNumericCastTests
{
    [Fact]
    public void EnumToIntCast_UsesOrdinal()
    {
        const string code = """
            public class C {
                enum Dim { Horizontal, Vertical }

                int[] a = new int[2];

                public int M(Dim d) {
                    return a[(int)d];
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("a[d.ordinal()]", result.GeneratedCode);
        Assert.DoesNotContain("(int)(d)", result.GeneratedCode);
    }
}
