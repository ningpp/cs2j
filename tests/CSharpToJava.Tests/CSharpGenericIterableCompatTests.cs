using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that plain Java List<T> (e.g. from Enumerable.Empty<T>() →
/// Collections.emptyList()) is automatically wrapped via
/// CSharpGenericIterable.from() when the target parameter type is
/// IEnumerable&lt;T&gt; (mapped to CSharpGenericIterable&lt;T&gt;).
/// </summary>
public class CSharpGenericIterableCompatTests
{
    private readonly ITestOutputHelper _out;

    public CSharpGenericIterableCompatTests(ITestOutputHelper output) => _out = output;

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
