using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Enumerable.Empty&lt;T&gt;() maps to Collections.emptyList() instead of
/// Stream.empty(), because Stream does not implement Iterable in Java and is
/// incompatible with materialized collections in ternary expressions.
/// </summary>
public class EnumerableEmptyMappingTests
{

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
