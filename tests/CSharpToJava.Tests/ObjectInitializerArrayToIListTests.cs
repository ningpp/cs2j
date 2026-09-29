using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# array values assigned to IList&lt;T&gt; properties via object
/// initializers are properly wrapped. In C#, T[] implements IList&lt;T&gt;,
/// so "new Foo { Nodes = arrayVar }" compiles. In Java, the converted
/// setNodes(CSharpGenericIList&lt;T&gt;) does not accept T[], so the array
/// must be wrapped via ArrayHelper.toList().
/// </summary>
public class ObjectInitializerArrayToIListTests
{
    private readonly ITestOutputHelper _out;

    public ObjectInitializerArrayToIListTests(ITestOutputHelper output) => _out = output;

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
