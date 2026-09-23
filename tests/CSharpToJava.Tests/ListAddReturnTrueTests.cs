using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that IList implementation's Add method is properly converted
/// when using structured body (IR) instead of raw Body string.
/// </summary>
public class ListAddReturnTrueTests
{
    private readonly ITestOutputHelper _out;
    public ListAddReturnTrueTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
