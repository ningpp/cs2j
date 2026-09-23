using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ helper methods use Iterable&lt;T&gt; for non-array collection parameters
/// rather than the concrete collection type (avoids type conflicts like java.util.Set vs custom Set).
/// </summary>
public class LinqHelperIterableParameterTests
{
    private readonly ITestOutputHelper _out;
    public LinqHelperIterableParameterTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions { PreferStreamApi = false },
        });
    }
}
