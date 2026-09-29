using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a user-defined generic method whose name collides (case-insensitively)
/// with JsonSerializer.DeserializeAsync&lt;T&gt; is NOT incorrectly marked as needing
/// a TypeReference&lt;T&gt; token. The user method may call a different method (e.g.
/// Serializer.DeserializeWrapper&lt;T&gt;) that expects Class&lt;T&gt;, so the token
/// type must remain Class&lt;T&gt; to match.
/// </summary>
public class JsonSerializerMethodNameCollisionTests
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
