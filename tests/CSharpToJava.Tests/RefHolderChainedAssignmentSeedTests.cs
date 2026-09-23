using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ref holder is seeded with the variable's current value
/// when the variable is assigned via a chained assignment before the
/// ref call — e.g. page = pageEnd = _pageCurrent; Method(ref pageEnd)
/// must produce new ObjectHolder<>(pageEnd), not new ObjectHolder<>().
/// </summary>
public class RefHolderChainedAssignmentSeedTests
{
    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
