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
