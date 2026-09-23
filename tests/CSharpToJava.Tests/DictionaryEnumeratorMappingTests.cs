using System;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that GetEnumerator() on a dictionary-like receiver is adapted
/// to the target enumerator type (IDictionaryEnumerator vs IEnumerator&lt;T&gt;).
/// </summary>
public class DictionaryEnumeratorMappingTests
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
