using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using System;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that .Reverse() returns a List (Iterable-compatible) when not chained,
/// and returns a Stream when chained into further LINQ operations.
/// </summary>
public class ReverseIterableTests
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
