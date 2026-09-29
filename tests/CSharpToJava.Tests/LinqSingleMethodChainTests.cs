using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for single-method LINQ chains without terminals
/// (e.g. standalone .Where(), .Select(), .SelectMany(), .OrderBy()).
/// These were previously skipped by the LinqRewriter with reason
/// "Single root method requires yield return".
/// </summary>
public class LinqSingleMethodChainTests
{
    private static ConversionResult ConvertProcedural(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                PreferStreamApi = false,
            },
        });
    }

    // ─── Standalone Where ───

    // ─── Standalone Select ───

    // ─── Standalone SelectMany ───

    // ─── Standalone OrderBy ───

    [Fact]
    public void StandaloneOrderBy_PassedToMethod_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    static IEnumerable<int> GetSorted(List<int> src) {
        return src.OrderBy(x => x);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // OrderBy must not leave raw C# method names in the output.
        // It may be procedurally rewritten (ProceduralLinq) or converted to stream API (.sorted()).
        Assert.DoesNotContain(".orderBy(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Standalone Where on custom collection ───
}
