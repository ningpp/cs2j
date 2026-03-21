using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumeratorResetFallbackTests
{
    [Fact]
    public void IEnumeratorReset_DoesNotEmitIteratorResetCall()
    {
        const string code = """
            using System.Collections;

            public class C {
                IEnumerator edges;

                void R() {
                    edges.Reset();
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("edges.reset()", result.GeneratedCode);
        Assert.Contains("reset unsupported for Java Iterator", result.GeneratedCode);
    }
}
