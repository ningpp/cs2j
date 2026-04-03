using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Phase 4 tests: DistinctBy, UnionBy, IntersectBy, ExceptBy, MinBy, MaxBy, Chunk.
/// </summary>
public class LinqProceduralPhase4Tests
{
    // ─── DistinctBy ───

    [Fact]
    public void DistinctBy_ToList_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var items = new List<string> { ""apple"", ""ant"", ""banana"", ""berry"" };
        return items.DistinctBy(x => x[0]).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctBy_WithWhere_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var items = new List<string> { ""apple"", ""ant"", ""banana"", ""berry"" };
        return items.Where(x => x.Length > 3).DistinctBy(x => x[0]).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── MinBy / MaxBy ───

    [Fact]
    public void MinBy_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    string M() {
        var items = new List<string> { ""hello"", ""hi"", ""hey"" };
        return items.MinBy(x => x.Length);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Comparer", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxBy_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    string M() {
        var items = new List<string> { ""hello"", ""hi"", ""hey"" };
        return items.MaxBy(x => x.Length);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Comparer", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MinBy_WithWhere_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    string M() {
        var items = new List<string> { ""hello"", ""hi"", ""hey"", ""howdy"" };
        return items.Where(x => x.Length > 2).MinBy(x => x.Length);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── UnionBy ───

    [Fact]
    public void UnionBy_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var first = new List<string> { ""apple"", ""banana"" };
        var second = new List<string> { ""avocado"", ""blueberry"" };
        return first.UnionBy(second, x => x[0]).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_unionSeen", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── IntersectBy ───

    [Fact]
    public void IntersectBy_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var items = new List<string> { ""apple"", ""banana"", ""cherry"" };
        var keys = new List<char> { 'a', 'c' };
        return items.IntersectBy(keys, x => x[0]).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_secondSet", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── ExceptBy ───

    [Fact]
    public void ExceptBy_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var items = new List<string> { ""apple"", ""banana"", ""cherry"" };
        var keys = new List<char> { 'a', 'c' };
        return items.ExceptBy(keys, x => x[0]).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_secondSet", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Chunk ───

    [Fact]
    public void Chunk_ToList_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int[]> M() {
        var items = new List<int> { 1, 2, 3, 4, 5 };
        return items.Chunk(2).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_chunkBuffer_", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_Count_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M() {
        var items = new List<int> { 1, 2, 3, 4, 5 };
        return items.Chunk(3).Count();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_WithWhere_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int[]> M() {
        var items = new List<int> { 1, 2, 3, 4, 5, 6, 7 };
        return items.Where(x => x > 1).Chunk(3).ToList();
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
