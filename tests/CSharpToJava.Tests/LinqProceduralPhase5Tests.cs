using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Phase 5 tests: Join and GroupJoin.
/// </summary>
public class LinqProceduralPhase5Tests
{
    // ─── Join ───

    [Fact]
    public void Join_Simple_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var outer = new List<int> { 1, 2, 3 };
        var inner = new List<string> { ""a1"", ""b2"", ""c3"", ""d1"" };
        return outer.Join(inner, o => o, i => int.Parse(i.Substring(1)), (o, i) => o + "":"" + i).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_joinLookup", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_WithWhere_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var people = new List<(string Name, int DeptId)> { (""Alice"", 1), (""Bob"", 2) };
        var depts = new List<(int Id, string Name)> { (1, ""Engineering""), (2, ""Sales"") };
        return people.Join(depts, p => p.DeptId, d => d.Id, (p, d) => p.Name + "" - "" + d.Name).Where(x => x.Length > 5).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_Count_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M() {
        var ids = new List<int> { 1, 2, 3 };
        var names = new List<(int Id, string Name)> { (1, ""A""), (2, ""B""), (3, ""C"") };
        return ids.Join(names, id => id, n => n.Id, (id, n) => n.Name).Count();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── GroupJoin ───

    [Fact]
    public void GroupJoin_Simple_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var depts = new List<(int Id, string Name)> { (1, ""Eng""), (2, ""Sales"") };
        var people = new List<(string Name, int DeptId)> { (""Alice"", 1), (""Bob"", 1), (""Carol"", 2) };
        return depts.GroupJoin(people, d => d.Id, p => p.DeptId, (d, ps) => d.Name + "":"" + ps.Count()).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_gjLookup", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupJoin_WithWhere_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<string> M() {
        var categories = new List<int> { 1, 2, 3 };
        var items = new List<(int CatId, string Name)> { (1, ""A""), (1, ""B""), (2, ""C"") };
        return categories.GroupJoin(items, c => c, i => i.CatId, (c, grp) => c + "":"" + grp.Count()).Where(x => x.Length > 2).ToList();
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
                PreferStreamApi = false,
            },
        });
    }
}
