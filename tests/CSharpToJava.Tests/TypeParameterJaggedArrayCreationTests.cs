using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TypeParameterJaggedArrayCreationTests
{
    [Fact]
    public void TypeParameterJaggedArray_CastsToFullArrayRank()
    {
        const string code = """
            public class G<TEdge>
            {
                TEdge[][] Build(int n)
                {
                    return new TEdge[n][];
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("(TEdge[][]) new Object[n][]", result.GeneratedCode);
        Assert.DoesNotContain("(TEdge[]) new Object[n][]", result.GeneratedCode);
    }

    [Fact]
    public void TypeParameterJaggedArray_UsesConstraintTypeForRuntimeArrayCreation()
    {
        const string code = """
            public interface IEdge {}

            public class G<TEdge> where TEdge : IEdge
            {
                TEdge[][] Build(int n)
                {
                    return new TEdge[n][];
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("(TEdge[][]) new IEdge[n][]", result.GeneratedCode);
        Assert.DoesNotContain("(TEdge[][]) new Object[n][]", result.GeneratedCode);
    }
}
