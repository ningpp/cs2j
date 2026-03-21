using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CollectionToArrayCastTests
{
    [Fact]
    public void CastIListToArray_UsesToArrayCall()
    {
        const string code = """
            using System.Collections.Generic;

            class LayerEdge {}

            class C {
                LayerEdge[] Convert(IList<LayerEdge> value) {
                    return (LayerEdge[])value;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("value.toArray(new LayerEdge[0])", result.GeneratedCode);
        Assert.DoesNotContain("(LayerEdge[])(value)", result.GeneratedCode);
    }
}
