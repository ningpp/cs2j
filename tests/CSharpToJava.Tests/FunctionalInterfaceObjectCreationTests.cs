using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class FunctionalInterfaceObjectCreationTests
{
    [Fact]
    public void DelegateConstruction_DoesNotInstantiateJavaFunctionInterface()
    {
        const string code = """
            using System;

            public class C {
                private Func<int, bool> f;
                bool IsPos(int x) => x > 0;
                public C() {
                    f = new Func<int, bool>(IsPos);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("new Function<", result.GeneratedCode);
        Assert.Contains("this::isPos", result.GeneratedCode);
    }
}
