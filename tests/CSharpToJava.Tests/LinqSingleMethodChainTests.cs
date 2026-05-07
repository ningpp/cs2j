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

    [Fact]
    public void StandaloneWhere_PassedToConstructor_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    static List<int> GetFiltered(List<int> src, List<int> filter) {
        return new List<int>(src.Where(x => filter.Contains(x)));
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Must be rewritten to procedural — no .where() or .Where() left behind
        Assert.DoesNotContain(".where(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Standalone Select ───

    [Fact]
    public void StandaloneSelect_PassedToConstructor_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    static List<int> GetMapped(List<int> src) {
        return new List<int>(src.Select(x => x * 2));
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain(".select(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    // ─── Standalone SelectMany ───

    [Fact]
    public void StandaloneSelectMany_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    static IEnumerable<int> GetFlat(List<List<int>> src) {
        return src.SelectMany(x => x);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain(".selectMany(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".SelectMany(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

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

    [Fact]
    public void StandaloneWhere_OnCustomCollection_ProducesProcedural()
    {
        // Simulates the MSAGL Set<T> pattern
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class MySet<T> : ICollection<T> {
    HashSet<T> _data = new HashSet<T>();
    public void Add(T item) { _data.Add(item); }
    public bool Contains(T item) { return _data.Contains(item); }
    public int Count => _data.Count;
    public bool IsReadOnly => false;
    public bool Remove(T item) { return _data.Remove(item); }
    public void Clear() { _data.Clear(); }
    public void CopyTo(T[] arr, int i) { _data.CopyTo(arr, i); }
    public IEnumerator<T> GetEnumerator() { return _data.GetEnumerator(); }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
    public MySet(IEnumerable<T> coll) { foreach (var x in coll) _data.Add(x); }
    public static MySet<T> Intersect(MySet<T> a, MySet<T> b) {
        return new MySet<T>(a.Count < b.Count
            ? a.Where(x => b.Contains(x))
            : b.Where(x => a.Contains(x)));
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain(".where(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }
}
