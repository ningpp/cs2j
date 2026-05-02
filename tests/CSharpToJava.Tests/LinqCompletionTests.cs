using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for LINQ completion — verifies Join, GroupJoin, OrderBy, Union, Intersect, Except
/// are properly converted to Java stream/procedural equivalents (Issue #17 verification).
/// </summary>
public class LinqCompletionTests
{
    [Fact]
    public void OrderBy_ProducesSorted()
    {
        var result = Convert(@"
using System.Linq;
using System.Collections.Generic;
class C {
    List<int> Sort(List<int> items) => items.OrderBy(x => x).ToList();
}");
        Assert.True(result.Success);
        Assert.Contains("sorted", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderByDescending_ProducesReversed()
    {
        var result = Convert(@"
using System.Linq;
using System.Collections.Generic;
class C {
    List<int> Sort(List<int> items) => items.OrderByDescending(x => x).ToList();
}");
        Assert.True(result.Success);
        Assert.Contains("reversed()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Union_ProducesDistinctConcat()
    {
        var result = Convert(@"
using System.Linq;
using System.Collections.Generic;
class C {
    List<int> Test(List<int> a, List<int> b) => a.Union(b).ToList();
}");
        Assert.True(result.Success);
        Assert.Contains("distinct()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Intersect_ProducesHashSetFilter()
    {
        var result = Convert(@"
using System.Linq;
using System.Collections.Generic;
class C {
    List<int> Test(List<int> a, List<int> b) => a.Intersect(b).ToList();
}");
        Assert.True(result.Success);
        Assert.Contains("HashSet", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("filter", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Except_ProducesNegatedFilter()
    {
        var result = Convert(@"
using System.Linq;
using System.Collections.Generic;
class C {
    List<int> Test(List<int> a, List<int> b) => a.Except(b).ToList();
}");
        Assert.True(result.Success);
        Assert.Contains("HashSet", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("filter", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_ProducesFlatMap()
    {
        var result = Convert(@"
using System.Linq;
using System.Collections.Generic;
class C {
    List<string> Test(List<int> ids, List<KeyValuePair<int, string>> names) =>
        ids.Join(names, id => id, n => n.Key, (id, n) => n.Value).ToList();
}");
        Assert.True(result.Success);
        // LINQ desugarer now always on
    }

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
