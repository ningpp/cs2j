using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ContainsLoopCaptureTests
{
    [Fact]
    public void EnumerableContains_InsideForLoop_DoesNotEmitCapturingAnyMatchLambda()
    {
        const string code = """
            using System.Linq;

            public class C {
                bool M(int[] except, int[] points) {
                    for (int i = 0; i < points.Length; ++i) {
                        if (!except.Contains(i)) {
                            return true;
                        }
                    }
                    return false;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("anyMatch(x ->", result.GeneratedCode);
        Assert.Contains("boxed().collect(Collectors.toSet()).contains(i)", result.GeneratedCode);
    }
}
