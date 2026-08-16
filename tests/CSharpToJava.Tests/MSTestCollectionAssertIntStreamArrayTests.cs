using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class MSTestCollectionAssertIntStreamArrayTests
{
    [Fact]
    public void AreEqual_IntStreamToArray_IsWrappedAsCollection()
    {
        var result = Convert("""
            using System.Collections.Generic;
            using System.Linq;
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            class Sample
            {
                void M(List<int> actual)
                {
                    CollectionAssert.AreEqual(Enumerable.Range(1, 100).ToArray(), actual);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("CollectionAssert.areEqual(IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CollectionAssert.areEqual(Arrays.stream(IntStream.range(", result.GeneratedCode, StringComparison.Ordinal);
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
