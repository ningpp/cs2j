using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that call sites of generic methods with trailing Class&lt;?&gt;
/// parameters (added because the method body creates new T[] or similar)
/// correctly emit the Class literal argument at the call site.
/// </summary>
public class TrailingClassTokenCallSiteTests
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
