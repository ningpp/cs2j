using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LambdaCapturedHolderInitializationTests
{
    [Fact]
    public void MutatedCapturedVariable_InLambda_HolderInitializerUsesOriginalVariable()
    {
        const string code = """
            using System;

            public class C {
                static void Visit(Action<int, int> f) { }

                public int M() {
                    int numCrossings = 0;
                    Visit((a, b) => {
                        if (a > b) numCrossings++;
                    });
                    return numCrossings;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("int[] _numCrossings = { numCrossings }", result.GeneratedCode);
        Assert.DoesNotContain("int[] _numCrossings = { _numCrossings[0] }", result.GeneratedCode);
    }
}
