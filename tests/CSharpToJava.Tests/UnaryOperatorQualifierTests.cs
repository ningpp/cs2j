using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class UnaryOperatorQualifierTests
{
    [Fact]
    public void UnaryOperator_OnNestedType_IsQualifiedWithNestedTypeName()
    {
        const string code = """
            namespace N {
                public class Outer {
                    public static class Complex {
                        public static Complex operator -(Complex a) => a;
                        public static Complex negate(Complex a) => a;
                    }

                    public Complex M(Complex a) {
                        return -a;
                    }
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Complex.negate(a)", result.GeneratedCode);
        Assert.DoesNotContain("return negate(a);", result.GeneratedCode);
    }
}
