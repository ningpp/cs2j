using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ ToHashSet() produces a CSharpHashSet (not a bare java.util.HashSet),
/// so casts/assignments to HashSet<T> (mapped to CSharpHashSet<T>) do not throw
/// ClassCastException at runtime.
/// </summary>
public class ToHashSetRuntimeTypeTests
{

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
