using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that unresolved types (IErrorTypeSymbol with no namespace) still get mapped
/// via the short-name fallback in TypeMappingRegistry.
/// Reproduces: InitialLayoutByCluster.java, MdsGraphLayout.java — ParallelOptions not mapped.
/// </summary>
public class UnresolvedTypeMappingFallbackTests
{
    /// <summary>
    /// ParallelOptions is not in the default compilation references but IS in TypeMappings.json.
    /// The converter should map it to Object via short-name fallback.
    /// </summary>
    [Fact]
    public void ParallelOptions_Unresolved_MappedToObject()
    {
        // ParallelOptions is from System.Threading.Tasks.Parallel which may not be
        // in the default compilation references, making it an IErrorTypeSymbol.
        // The mapping ParallelOptions → Object should still apply.
        var result = Convert(@"
class MyClass
{
    void Run()
    {
        ParallelOptions options = new ParallelOptions();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("ParallelOptions", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the unresolved type has a namespace, the FQN mapping should work.
    /// </summary>
    [Fact]
    public void ParallelOptions_WithUsing_MappedToObject()
    {
        var result = Convert(@"
using System.Threading.Tasks;
class MyClass
{
    ParallelOptions options;
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
