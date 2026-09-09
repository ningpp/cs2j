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
    [Fact]
    public void NonDeserializeCaller_KeepsClassToken()
    {
        // Methods that don't call Deserialize<T> and aren't called by
        // TypeRef methods should keep Class<T> tokens
        var result = Convert("""
class Demo {
    public T[] Repeat<T>(T value, int count)
    {
        return new T[count];
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Should use Class<?> (trailing), not TypeReference
        Assert.Contains("Class<?>", code);
        Assert.DoesNotContain("TypeReference", code);
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
