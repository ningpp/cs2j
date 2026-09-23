using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that arrays produced by LINQ operations (e.g. Enumerable.Reverse().ToArray())
/// are properly wrapped when passed to CollectionAssert.AreEqual, which expects
/// Iterable&lt;?&gt; in Java. The key issue: the transformed expression starts with
/// "Arrays.stream(" but ends with ".toArray()", producing an array, not an Iterable.
/// </summary>
public class CollectionAssertArrayArgTests
{
    private readonly ITestOutputHelper _out;

    public CollectionAssertArrayArgTests(ITestOutputHelper output) => _out = output;

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
