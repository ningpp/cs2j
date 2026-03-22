using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DefaultExpressionPrimitiveTests
{
    [Fact]
    public void DefaultDouble_EmitsPrimitiveZeroLiteral()
    {
        const string code = """
            public class C
            {
                public bool M(double x)
                {
                    return x == default(double);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("x == 0.0", result.GeneratedCode);
        Assert.DoesNotContain("new double()", result.GeneratedCode);
    }
}