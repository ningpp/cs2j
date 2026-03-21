using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArrayCopyToConversionTests
{
    [Fact]
    public void ArrayCopyTo_UsesArrayLength_NotToArraySize()
    {
        const string code = """
            public class C {
                public void M(double[] src, double[] dst) {
                    src.CopyTo(dst, 0);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("System.arraycopy(src, 0, dst, 0, src.length)", result.GeneratedCode);
        Assert.DoesNotContain("src.toArray()", result.GeneratedCode);
        Assert.DoesNotContain("src.size()", result.GeneratedCode);
    }
}
