using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Phase 9 tests: .NET 7+/9+ new methods — Order() and OrderDescending().
/// </summary>
public class LinqProceduralPhase9Tests
{
    [Fact]
    public void Order_WithWhere_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M(List<int> items) {
        return items.Where(x => x > 0).Order().ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_sortBuffer", result.GeneratedCode, StringComparison.Ordinal);
        // .Sort() is mapped to Java sort syntax (e.g., Collections.sort or .sort(null))
        Assert.Contains("sort", result.GeneratedCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDescending_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M(List<int> items) {
        return items.Where(x => x > 0).OrderDescending().ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        // .Reverse() is mapped to Java syntax
        Assert.Contains("_sortBuffer", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Order_WithSelect_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M(List<int> items) {
        return items.Order().Select(x => x * 2).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Order_Count_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.Order().Count();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
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
                EmitCompatibilityHelpers = false,
                PreferStreamApi = false,
            },
        });
    }
}
