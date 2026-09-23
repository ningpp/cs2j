using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Phase 7 tests: AsEnumerable passthrough and materialization/generator methods.
/// </summary>
public class LinqProceduralPhase7Tests
{
    // ─── AsEnumerable passthrough (procedural) ───

    [Fact]
    public void AsEnumerable_InChain_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M(List<int> items) {
        return items.Where(x => x > 0).AsEnumerable().Select(x => x * 2).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AsEnumerable_BeforeTerminal_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.Where(x => x > 0).AsEnumerable().Count();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AsEnumerable_Stream_IsStripped()
    {
        var result = ConvertStream(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M(List<int> items) {
        return items.AsEnumerable().Where(x => x > 0).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain("AsEnumerable", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Helpers ───

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

    private static ConversionResult ConvertStream(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            },
        });
    }
}
