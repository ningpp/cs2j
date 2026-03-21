using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ObjectInitializerLambdaPreStatementTests
{
    [Fact]
    public void ObjectInitializer_WithLambdaProperty_DoesNotCaptureTempVarIntoLambda()
    {
        const string code = """
            using System;

            public class G {
                public Func<int, int> F { get; set; }
            }

            public class C {
                G M() {
                    return new G { F = x => x + 1 };
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("-> {\n        var _obj", result.GeneratedCode);
        Assert.Contains("setF", result.GeneratedCode);
    }
}
