using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ExpressionLambdaCaptureAssignmentTests
{
    [Fact]
    public void ExpressionBodyLambda_AssigningCapturedLocal_UsesHolderArray()
    {
        const string code = """
            using System;

            public class C {
                static void Run(Action a) { }

                public int M() {
                    int x = 0;
                    Run(() => x = 5);
                    return x;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("int[] _x = { x }", result.GeneratedCode);
        Assert.Contains("() -> _x[0] = 5", result.GeneratedCode);
    }
}
