using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that when a generic method's body calls JsonSerializer.Deserialize&lt;T&gt;,
/// the TypeReference token type propagates to hoisted local functions that
/// forward the same type parameter, even when the local function itself
/// does not call Deserialize&lt;T&gt; directly.
/// </summary>
public class LocalFunctionTypeRefPropagationTests
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
