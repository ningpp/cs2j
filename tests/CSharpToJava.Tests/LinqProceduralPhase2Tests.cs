using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Phase 2 tests: indexed overloads for Where, Select, SkipWhile,
/// TakeWhile, and SelectMany.
/// </summary>
public class LinqProceduralPhase2Tests
{
    // ─── Indexed Where ───

    [Fact]
    public void Where_Indexed_ToList_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var items = new List<int> { 10, 20, 30, 40, 50 };
        return items.Where((x, i) => i % 2 == 0).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Where_Indexed_WithChain_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var items = new List<int> { 10, 20, 30, 40, 50 };
        return items.Where((x, i) => i < 3 && x > 10).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Indexed Select ───

    [Fact]
    public void Select_Indexed_ToList_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var items = new List<string> { ""a"", ""b"", ""c"" };
        return items.Select((x, i) => x + i).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_Indexed_Count_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M() {
        var items = new List<int> { 1, 2, 3, 4, 5 };
        return items.Select((x, i) => x * i).Count();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Indexed SkipWhile ───

    [Fact]
    public void SkipWhile_Indexed_ToList_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var items = new List<int> { 10, 20, 30, 40, 50 };
        return items.SkipWhile((x, i) => i < 2).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Indexed TakeWhile ───

    [Fact]
    public void TakeWhile_Indexed_ToList_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var items = new List<int> { 10, 20, 30, 40, 50 };
        return items.TakeWhile((x, i) => i < 3).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── SelectMany (non-indexed, as intermediate) ───

    [Fact]
    public void SelectMany_ToList_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var items = new List<List<int>> { new List<int>{1,2}, new List<int>{3,4} };
        return items.SelectMany(x => x).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectMany_WithWhere_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var items = new List<List<int>> { new List<int>{1,2,3}, new List<int>{4,5,6} };
        return items.SelectMany(x => x).Where(x => x > 2).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Indexed SelectMany ───

    [Fact]
    public void SelectMany_Indexed_ToList_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var items = new List<List<int>> { new List<int>{1,2}, new List<int>{3,4} };
        return items.SelectMany((x, i) => x).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
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
}
