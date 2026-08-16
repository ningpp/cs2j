using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: converting <c>result.First().Contains(1)</c> where the collection
/// element is <c>int[]</c> produced
/// <c>result.stream().findFirst().orElseThrow().boxed().collect(...)</c>.
/// The terminal <c>findFirst().orElseThrow()</c> returns the <c>int[]</c> element,
/// not an <c>IntStream</c>, so <c>.boxed()</c> cannot be called on it and the
/// generated Java does not compile:
/// <code>
/// CollectionTests.java:[525,54] 找不到符号
///   符号:   方法 boxed()
///   位置: 类 int[]
/// </code>
/// </summary>
public class FirstElementContainsOnArrayTests
{
    [Fact]
    public void ContainsOnIntArrayElementFromFirst_DoesNotAppendBoxedToArray()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample
{
    bool M(ISet<int[]> result)
    {
        return result.First().Contains(1);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // findFirst().orElseThrow() returns the int[] element itself; appending
        // .boxed() to it is invalid Java (int[].boxed() does not exist).
        Assert.DoesNotContain(".orElseThrow().boxed()", result.GeneratedCode, StringComparison.Ordinal);

        // The primitive array must be wrapped before boxing so the generated code compiles.
        Assert.Contains("Arrays.stream(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainsOnIntArrayElementFromLast_DoesNotAppendBoxedToArray()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Sample
{
    bool M(ISet<int[]> result)
    {
        return result.Last().Contains(1);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // Same bug family: reduce((a, b) -> b).orElseThrow() returns the int[] element.
        Assert.DoesNotContain(".orElseThrow().boxed()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Arrays.stream(", result.GeneratedCode, StringComparison.Ordinal);
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
