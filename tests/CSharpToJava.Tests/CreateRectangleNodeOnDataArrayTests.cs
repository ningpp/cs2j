using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CreateRectangleNodeOnDataArrayTests
{
    [Fact]
    public void ArrayArgument_IsWrappedForIEnumerableParameter()
    {
        var result = Convert("""
            using System;
            using System.Collections.Generic;

            interface IRectangle<P> { }

            class RectangleNode<T, P>
            {
                public static RectangleNode<T, P> CreateRectangleNodeOnData(
                    IEnumerable<T> data,
                    Func<T, IRectangle<P>> rectangleDelegate) => null;
            }

            class Sample
            {
                void M()
                {
                    RectangleNode<string, object>.CreateRectangleNodeOnData(
                        new[] { "a", "b" },
                        data => null);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("createRectangleNodeOnData(new String[]", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ArrayHelper.toList(", result.GeneratedCode, StringComparison.Ordinal);
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
