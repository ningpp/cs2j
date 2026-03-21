using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PatternEnumeratorIterableBridgeTests
{
    [Fact]
    public void ClassWithGetEnumeratorPattern_ImplementsIterableInJava()
    {
        const string code = """
            using System.Collections.Generic;

            public class Succ {
                public IEnumerator<int> GetEnumerator() {
                    return ((IEnumerable<int>)new List<int> { 1, 2 }).GetEnumerator();
                }
            }

            public class C {
                void M() {
                    foreach (var v in new Succ()) {
                    }
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("class Succ", result.GeneratedCode);
        Assert.Contains("implements Iterable<", result.GeneratedCode);
    }
}
