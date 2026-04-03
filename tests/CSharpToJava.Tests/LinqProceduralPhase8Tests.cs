using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Phase 8 tests: OrDefault with custom default value, LastOrDefault parameterless fix.
/// </summary>
public class LinqProceduralPhase8Tests
{
    // ─── FirstOrDefault with custom default ───

    [Fact]
    public void FirstOrDefault_WithDefault_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.Where(x => x > 100).FirstOrDefault(-1);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_defaultValue", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstOrDefault_WithConditionAndDefault_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.FirstOrDefault(x => x > 100, -1);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_defaultValue", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── LastOrDefault with custom default ───

    [Fact]
    public void LastOrDefault_WithDefault_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.Where(x => x > 0).LastOrDefault(-1);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_defaultValue", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LastOrDefault_WithConditionAndDefault_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.LastOrDefault(x => x > 100, -1);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── SingleOrDefault with custom default ───

    [Fact]
    public void SingleOrDefault_WithDefault_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.Where(x => x == 42).SingleOrDefault(-1);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_defaultValue", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleOrDefault_WithConditionAndDefault_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.SingleOrDefault(x => x == 42, -1);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Fix: LastOrDefault() parameterless ───

    [Fact]
    public void LastOrDefault_Parameterless_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.Where(x => x > 0).LastOrDefault();
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
