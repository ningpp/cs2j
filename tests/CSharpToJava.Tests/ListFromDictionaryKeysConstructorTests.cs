using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that new List(dictionary.Keys) produces new CSharpList(dictionary.keySet())
/// without an unnecessary (Iterable) cast that would break the CSharpList(Collection) constructor.
/// </summary>
public class ListFromDictionaryKeysConstructorTests
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
