using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// A local declared with <c>var</c> from a generic tuple struct
/// (<c>Cs2jTuples.TupleT0T1&lt;T0, T1&gt;</c>) must emit the explicit mapped type.
/// javac can infer such a <c>var</c> as the raw tuple type when the returning
/// method is compiled in the same batch from another file, degrading
/// <c>ItemN</c> to <c>Object</c> (real chain: aglningpptests MdsPerformanceTest
/// assigns <c>Item2</c> to <c>double[][]</c>).
/// </summary>
public class TupleStructVarLocalTypeTests
{
    private const string TupleStructSource = """
        namespace Cs2jTuples
        {
            public struct TupleT0T1<T0, T1>
            {
                public T0 Item1;
                public T1 Item2;
                public TupleT0T1(T0 item1, T1 item2) { Item1 = item1; Item2 = item2; }
            }
        }
        """;

    [Fact]
    public async Task VarTupleStructLocal_DeclaresExplicitParameterizedType()
    {
        var results = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "TupleStruct.cs", Content = TupleStructSource },
            new SourceFile { FilePath = "Helper.cs", Content = """
                namespace Demo
                {
                    public static class Helper
                    {
                        public static Cs2jTuples.TupleT0T1<int[], double[][]> M()
                            => new Cs2jTuples.TupleT0T1<int[], double[][]>(new int[1], new double[1][]);
                    }
                }
                """ },
            new SourceFile { FilePath = "Test.cs", Content = """
                namespace Demo
                {
                    public class Test
                    {
                        public int Use()
                        {
                            var t = Helper.M();
                            double[][] d = t.Item2;
                            return d.Length + t.Item1.Length;
                        }
                    }
                }
                """ },
        });

        var test = Assert.Single(results, r => r.FileName == "Test.java");
        Assert.True(test.Success, string.Join("\n", test.Diagnostics) + "\n---Generated---\n" + test.GeneratedCode);
        Assert.Contains("TupleT0T1<int[], double[][]> t = ", test.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("var t = ", test.GeneratedCode, StringComparison.Ordinal);
        // The element access must stay parameterized: `d` is double[][], not Object.
        Assert.Contains("double[][] d = ", test.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Object d = ", test.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainVarLocal_StillUsesVar()
    {
        var results = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Sample.cs", Content = """
                using System.Collections.Generic;

                namespace Demo
                {
                    public class Sample
                    {
                        public int M()
                        {
                            var x = new List<int>();
                            return x.Count;
                        }
                    }
                }
                """ },
        });

        var sample = Assert.Single(results, r => r.FileName == "Sample.java");
        Assert.True(sample.Success, string.Join("\n", sample.Diagnostics) + "\n---Generated---\n" + sample.GeneratedCode);
        Assert.Contains("var x = ", sample.GeneratedCode, StringComparison.Ordinal);
    }

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
