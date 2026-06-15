using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class MSTestCollectionAssertConversionTests
{
    [Fact]
    public void CollectionAssertAreEqual_PrimitiveArrayExpected_WrapsArrayForIterable()
    {
        var result = Convert("""
            using System.Collections.Generic;
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            class Sample
            {
                void M(List<int> actual)
                {
                    int[] expected = new int[] { 1, 2, 3 };
                    CollectionAssert.AreEqual(expected, actual);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains(
            "CollectionAssert.areEqual(Arrays.stream(expected).boxed().collect(",
            result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CollectionAssert.areEqual(expected, actual)",
            result.GeneratedCode,
            StringComparison.Ordinal);
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
