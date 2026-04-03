using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Phase 1 tests: procedural expansion for Zip, SequenceEqual,
/// and ToDictionary (key-only overload).
/// </summary>
public class LinqProceduralPhase1Tests
{
    // ─── Zip ───

    [Fact]
    public void Zip_ToList_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var a = new List<int> { 1, 2, 3 };
        var b = new List<int> { 10, 20, 30 };
        return a.Zip(b, (x, y) => x + y).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Zip_WithWhereChain_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var a = new List<int> { 1, 2, 3, 4 };
        var b = new List<int> { 10, 20, 30, 40 };
        return a.Where(x => x > 1).Zip(b, (x, y) => x + y).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Zip_Count_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M() {
        var a = new List<int> { 1, 2, 3 };
        var b = new List<int> { 10, 20, 30 };
        return a.Zip(b, (x, y) => x + y).Count();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Zip_CapturedVariable_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M() {
        var a = new List<int> { 1, 2, 3 };
        var b = new List<int> { 10, 20, 30 };
        int offset = 5;
        return a.Zip(b, (x, y) => x + y + offset).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("offset", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── SequenceEqual ───

    [Fact]
    public void SequenceEqual_IntLists_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    bool M() {
        var a = new List<int> { 1, 2, 3 };
        var b = new List<int> { 1, 2, 3 };
        return a.SequenceEqual(b);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SequenceEqual_WithWhereChain_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    bool M() {
        var a = new List<int> { 1, 2, 3, 4 };
        var b = new List<int> { 2, 3, 4 };
        return a.Where(x => x > 1).SequenceEqual(b);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SequenceEqual_StringLists_UsesComparer()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    bool M() {
        var a = new List<string> { ""a"", ""b"" };
        var b = new List<string> { ""a"", ""b"" };
        return a.SequenceEqual(b);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── ToDictionary (key-only overload) ───

    [Fact]
    public void ToDictionary_KeyOnly_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    Dictionary<int, string> M() {
        var items = new List<string> { ""a"", ""bb"", ""ccc"" };
        return items.ToDictionary(x => x.Length);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToDictionary_KeyOnly_WithWhere_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    Dictionary<int, string> M() {
        var items = new List<string> { ""a"", ""bb"", ""ccc"" };
        return items.Where(x => x.Length > 1).ToDictionary(x => x.Length);
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
