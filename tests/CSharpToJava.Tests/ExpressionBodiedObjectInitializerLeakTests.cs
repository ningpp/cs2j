using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ExpressionBodiedObjectInitializerLeakTests
{
    [Fact]
    public void ExpressionBodiedFactory_WithObjectInitializer_DoesNotLeakIntoConstructor()
    {
        const string code = """
            public struct OverlappedEdge {
                public int source;
                public int target;
                public double overlapFactor;
                public double idealDistance;
                public double weight;

                public static OverlappedEdge Create(int source, int target, double overlapFactor, double idealDistance, double weight)
                    => new OverlappedEdge {
                        source = source,
                        target = target,
                        overlapFactor = overlapFactor,
                        idealDistance = idealDistance,
                        weight = weight
                    };
            }

            public class C {
                public C() { }
                public int M() {
                    var e = OverlappedEdge.Create(1, 2, 3, 4, 5);
                    return e.source;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("OverlappedEdge create(", result.GeneratedCode);
        Assert.Contains("return _obj", result.GeneratedCode);
        Assert.DoesNotContain("public C() { var _obj", result.GeneratedCode);
        Assert.DoesNotContain("public C() { _obj", result.GeneratedCode);
    }
}
