using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# generic method calls with explicit type arguments
/// (e.g. LoadJson&lt;BugsReference&gt;("path")) correctly insert the
/// Class&lt;T&gt; token argument in the Java output.
/// </summary>
public class GenericMethodExplicitTypeArgTests
{
    private readonly ITestOutputHelper _out;

    public GenericMethodExplicitTypeArgTests(ITestOutputHelper output) => _out = output;

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
