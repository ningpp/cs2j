using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.Threading.Tasks.ParallelOptions is properly mapped to Object in Java.
/// ParallelOptions has no Java equivalent; the parallel execution model differs.
/// </summary>
public class ParallelOptionsMappingTests
{
    /// <summary>
    /// Reproduces: "找不到符号: 类 ParallelOptions"
    /// when ParallelOptions is used as a field type.
    /// </summary>
    [Fact]
    public void ParallelOptions_FieldDeclaration_MappedToObject()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class Layout
{
    ParallelOptions parallelOptions;
    void Run()
    {
        parallelOptions = new ParallelOptions();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("ParallelOptions", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// ParallelOptions as local variable.
    /// </summary>
    [Fact]
    public void ParallelOptions_LocalVariable_MappedToObject()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class Sample
{
    void M()
    {
        ParallelOptions options = new ParallelOptions();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("ParallelOptions", result.GeneratedCode, StringComparison.Ordinal);
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
