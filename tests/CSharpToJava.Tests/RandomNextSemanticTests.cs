using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RandomNextSemanticTests
{
    private static ConversionResult Convert(string code)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = code });
    }

    [Fact]
    public void RandomNext_NoArg_MapsToJava21BoundedNextInt()
    {
        const string code = """
            using System;

            class C
            {
                int M(Random r)
                {
                    return r.Next();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("r.nextInt(0, Integer.MAX_VALUE)", result.GeneratedCode);
    }

    [Fact]
    public void RandomNext_MaxValue_PreservesNonPositiveEdgeCase()
    {
        const string code = """
            using System;

            class C
            {
                int M(Random r, int max)
                {
                    return r.Next(max);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("((max) <= 0 ? 0 : r.nextInt(max))", result.GeneratedCode);
    }

    [Fact]
    public void RandomNext_MinMax_PreservesEqualOrInvertedBounds()
    {
        const string code = """
            using System;

            class C
            {
                int M(Random r, int min, int max)
                {
                    return r.Next(min, max);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("((min) >= (max) ? (min) : r.nextInt(min, max))", result.GeneratedCode);
    }
}
