using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that dictionary initializer syntax ([key] = value) is correctly
/// converted to Java .put(key, value) calls instead of emitting
/// /* TODO: ImplicitElementAccess */ comments.
/// </summary>
public class ImplicitElementAccessTests
{
    [Fact]
    public void DictionaryInitializer_IndexerAssignment_GeneratesPutCall()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Test
{
    public void M()
    {
        var dict = new Dictionary<string, string>
        {
            [""key1""] = ""value1"",
            [""key2""] = ""value2""
        };
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;

        // Should NOT contain the TODO comment for ImplicitElementAccess
        Assert.DoesNotContain("TODO: ImplicitElementAccess", code);
        // Should generate .put() calls for the dictionary initializer
        Assert.Contains(".put(", code);
    }

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
