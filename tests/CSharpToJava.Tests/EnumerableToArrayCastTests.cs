using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumerableToArrayCastTests
{
    [Fact]
    public void CastIEnumerableToArray_UsesStreamSupportToArray()
    {
        const string code = """
            using System.Collections.Generic;

            class LayerEdge {}

            class C {
                LayerEdge[] Convert(IEnumerable<LayerEdge> value) {
                    return (LayerEdge[])value;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("StreamSupport.stream(value.spliterator(), false).toArray", result.GeneratedCode);
        Assert.DoesNotContain("value.toArray(new LayerEdge[0])", result.GeneratedCode);
    }
}
