using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Stream expressions are properly collected when assigned to fields/variables
/// declared as Iterable/Collection, or returned from lambda expressions expecting Iterable.
/// In Java, Stream does NOT implement Iterable.
/// </summary>
public class StreamToIterableAssignmentTests
{
    /// <summary>
    /// Reproduces: "Stream&lt;Edge&gt; cannot convert to Iterable&lt;Edge&gt;"
    /// when LINQ Where result is assigned to a field typed as IEnumerable.
    /// </summary>

    /// <summary>
    /// Reproduces: "Iterable&lt;Edge&gt; → cannot pass to ArrayList constructor"
    /// when new ArrayList is used with an Iterable (not Collection) argument.
    /// In Java, ArrayList accepts Collection, not Iterable.
    /// </summary>

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
