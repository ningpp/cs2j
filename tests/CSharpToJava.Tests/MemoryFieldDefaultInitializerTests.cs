using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# Memory&lt;byte&gt; is a struct and defaults to an empty memory, never null.
/// The Java ReadOnlyMemory&lt;Integer&gt; compat type is a reference type, so a
/// field without an initializer must be created explicitly. Regression:
/// Utf8JsonWriter._memory NPE in ArrayTests write paths.
/// </summary>
public class MemoryFieldDefaultInitializerTests
{
    [Fact]
    public void MemoryByteField_WithoutInitializer_GetsEmptyInstance()
    {
        var result = Convert(@"
using System;

class C
{
    private Memory<byte> _memory;
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ReadOnlyMemory<Integer> _memory = new ReadOnlyMemory<Integer>();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadOnlyMemory<Integer> _memory;", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
