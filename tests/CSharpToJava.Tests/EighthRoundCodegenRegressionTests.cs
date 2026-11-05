using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Eighth-round regressions: List&lt;T&gt;[] → IList&lt;T&gt;[] array adaptation,
/// primitive-wrapper method groups (int.Parse, double.IsNaN), explicit generic
/// type arguments on a var initializer, and explicit type arguments for the
/// indexed SelectMany adapter.
/// </summary>
public class EighthRoundCodegenRegressionTests
{
    [Fact]
    public void ListArray_IntoCSharpIListArraySlot_UsesFromArray()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Sample
            {
                static void Take(IList<int>[] lists) { }

                static List<int>[] Build() { return new List<int>[0]; }

                public static void M()
                {
                    Take(Build());
                }
            }
            """);

        AssertConverted(result, "CSharpIList.fromArray(");
        Assert.Contains("import io.github.ningpp.compat.CSharpIList;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntParseMethodGroup_MapsToIntegerParseInt()
    {
        var result = Convert("""
            using System.Linq;

            class Sample
            {
                public static int[] M(string[] values)
                {
                    return values.Select(int.Parse).ToArray();
                }
            }
            """);

        AssertConverted(result, "Integer::parseInt");
    }

    [Fact]
    public void ExplicitGenericTypeArguments_ProduceTypedVarDeclaration()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Case
            {
                public int Id;
            }

            static class Support
            {
                public static T LoadJson<T>(string path) { return default(T); }
            }

            class Sample
            {
                public static int M(string path)
                {
                    var baselines = Support.LoadJson<List<Case>>(path);
                    return baselines.Count;
                }
            }
            """);

        AssertConverted(result, "CSharpList<Case> baselines");
        Assert.DoesNotContain("var baselines", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexedSelectMany_EmitsExplicitTypeArguments()
    {
        var result = Convert("""
            using System.Collections.Generic;
            using System.Linq;

            class Sample
            {
                static IEnumerable<double> Expand(int value, int index)
                {
                    return new double[] { value, index };
                }

                public static double M(int[] nodes)
                {
                    return nodes.SelectMany(Expand).Min();
                }
            }
            """);

        AssertConverted(result, "new IndexedFlatMapFunction<Integer, Double>(");
        Assert.DoesNotContain("IndexedFlatMapFunction<>(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleIsNaMethodGroup_LowersToLambda()
    {
        var result = Convert("""
            using System.Linq;

            class Sample
            {
                public static bool M(double[] values)
                {
                    return values.Any(double.IsNaN);
                }
            }
            """);

        AssertConverted(result, "Double.isNaN(");
        Assert.Contains("__cs2jV", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Double::isNaN", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static void AssertConverted(ConversionResult result, string expected)
    {
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains(expected, result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
