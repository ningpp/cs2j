using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ParallelForEachConversionTests
{
    [Fact]
    public void ParallelForEach_WithMethodGroup_UsesStreamSupportForEach()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            public class C {
                void LayoutCluster(int x) {}

                void M(List<int> xs, ParallelOptions options) {
                    Parallel.ForEach(xs, options, LayoutCluster);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("StreamSupport.stream(xs.spliterator(), true).forEach(this::layoutCluster)", result.GeneratedCode);
        Assert.DoesNotContain("Parallel.forEach", result.GeneratedCode);
    }

    [Fact]
    public void ParallelForEach_WithLambda_DoesNotEmitEmptyMethodReference()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            public class C {
                void LayoutComponent(int offset, int x) {}

                void M(List<int> xs, ParallelOptions options, int settings) {
                    Parallel.ForEach(xs, options, x => LayoutComponent(settings, x));
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("StreamSupport.stream(xs.spliterator(), true).forEach", result.GeneratedCode);
        Assert.DoesNotContain("forEach(this::)", result.GeneratedCode);
        Assert.DoesNotContain("Parallel.forEach", result.GeneratedCode);
    }
}
