using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class VectorArrayCtorRegressionTests
{
    [Fact]
    public void LocalVectorCtor_WithDoubleArray_DoesNotWrapAsCollection()
    {
        const string code = """
            namespace N {
                public class Vector {
                    public double[] array;
                    public Vector(double[] array) { this.array = array; }
                }

                public class S {
                    public static Vector M(double[] b, double[] x) {
                        return new Vector((double[])x.Clone());
                    }
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new Vector((double[])(x.clone()))", result.GeneratedCode);
        Assert.DoesNotContain("new Vector(java.util.Arrays.stream", result.GeneratedCode);
        Assert.DoesNotContain("boxed().collect", result.GeneratedCode);
    }
}
