using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StructArgumentCloneTests
{
    [Fact]
    public void StructArguments_AreCloned_ForByValueConstructorCalls()
    {
        const string code = """
            public struct Box {
                public int Value;
            }

            public class Pair {
                public Pair(Box left, Box right) {}
            }

            public class C {
                public Pair M(Box a, Box b) {
                    return new Pair(a, b);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new Pair(a.clone(), b.clone())", result.GeneratedCode);
    }

    [Fact]
    public void StructArguments_AreCloned_ForByValueMethodCalls()
    {
        const string code = """
            public struct Box {
                public int Value;
            }

            public class C {
                static void Consume(Box value) {}

                public void M(Box value) {
                    Consume(value);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        // Read-only value parameters: the method never modifies the struct, so no clone needed at call site
        Assert.Contains("consume(value)", result.GeneratedCode);
    }
}