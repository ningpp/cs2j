using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DelegateCtorInstantiationTests
{
    [Fact]
    public void DelegateConstruction_DoesNotInstantiateFunctionalInterface()
    {
        const string code = """
            using System.Collections.Generic;

            public delegate IEnumerable<int> Points();

            public class C {
                IEnumerable<int> Corners() { yield break; }

                void M() {
                    var p = new Points(Corners);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("new Points(", result.GeneratedCode);
    }
}
