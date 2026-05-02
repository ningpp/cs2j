using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that auto-properties emitted as fields in Java are accessed as fields, not getters.
/// </summary>
public class AutoPropertyFieldAccessTests
{
    [Fact]
    public void AutoProperty_AccessedAsGetter_InSingleFilePipeline()
    {
        var result = Convert(@"
class Node {
    public object AlgorithmData { get; set; }
}
class Test {
    void M(Node n) {
        var data = n.AlgorithmData;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Single-file pipeline correctly generates getter for auto-property
        Assert.Contains("getAlgorithmData()", result.GeneratedCode, StringComparison.Ordinal);
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
