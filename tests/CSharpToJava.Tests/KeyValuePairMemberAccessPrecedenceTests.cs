using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that new KeyValuePair&lt;K,V&gt;(...).Key generates correct member access
/// using CSharpKeyValuePair's getValue() method.
/// </summary>
public class KeyValuePairMemberAccessPrecedenceTests
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
