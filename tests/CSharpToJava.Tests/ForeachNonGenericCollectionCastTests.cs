using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that foreach over a non-generic ICollection/IEnumerable source with a typed
/// loop variable uses a wildcard double-cast so javac accepts it.
/// </summary>
public class ForeachNonGenericCollectionCastTests
{
    [Fact]
    public void Foreach_OverNonGenericICollection_WithTypedVariable_UsesWildcardDoubleCast()
    {
        var result = Convert(@"
using System.Collections;

class Container
{
    public ICollection Values { get; }
}

class Sample
{
    void M(Container c)
    {
        foreach (string x in c.Values)
        {
            string y = x;
        }
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("for (String x : (Iterable<String>)(Iterable<?>)(c.getValues()))", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Iterable<String>) (c.getValues())", result.GeneratedCode, StringComparison.Ordinal);
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
