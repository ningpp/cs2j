using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the byte/span/pointer/ValueTask conversion cluster found
/// while building the converted System.Text.Json project:
/// 1. ReadOnlyMemory&lt;byte&gt;.ToArray() must materialize byte[] (not Integer[]).
/// 2. Non-async ValueTask-returning methods expose the underlying CompletableFuture.
/// 3. Casting a pointer (MemorySegment) to int must use .address().
/// 4. fixed (byte* p = &amp;MemoryMarshal.GetReference(span)) must pin the whole span.
/// 5. Target-typed span conditionals must not be double-wrapped (new Span<>(Span<>)).
/// </summary>
public class JsonByteSpanPointerValueTaskTests
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
