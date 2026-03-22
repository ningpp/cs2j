using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RuntimeExceptionMappingTests
{
    [Fact]
    public void NewException_MapsToRuntimeException()
    {
        const string code = """
            using System;
            class C {
                void M() {
                    throw new Exception("x");
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new RuntimeException(\"x\")", result.GeneratedCode);
        Assert.DoesNotContain("new Exception(\"x\")", result.GeneratedCode);
    }
}
