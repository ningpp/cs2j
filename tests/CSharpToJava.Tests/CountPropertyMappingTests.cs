using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# Count property on ICollection/IList is mapped to size() in Java.
/// Reproduces: "找不到符号: 变量 Count 位置: 接口 java.util.Collection"
/// </summary>
public class CountPropertyMappingTests
{
    [Fact]
    public void ICollection_Count_MappedToSize_DirectAccess()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test
{
    void M(ICollection<int> items)
    {
        int n = items.Count;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".Count", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".size()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LinqCount_OnEnumerable_MappedToSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Test
{
    void M(IEnumerable<int> items)
    {
        int n = items.Count();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".Count", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LinqCount_OnList_MappedToSize()
    {
        // Note: Linq Count() on List produces stream().count() — different from property .Count
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Test
{
    void M(List<string> items)
    {
        int n = items.Count();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain(".Count", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reproduces: AGL "找不到符号: 变量 Count 位置: 接口 java.util.Collection"
    /// The LINQ rewriter generates (items as ICollection&lt;T&gt;)?.Count pattern for
    /// Count() on IEnumerable with custom type T. In project compilation the semantic model
    /// may not resolve the MemberBindingExpression symbol.
    /// </summary>
    [Fact]
    public void LinqCount_OnEnumerable_WithCustomType_MappedToSize()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

interface IConstraint { int Level { get; } }

class Test
{
    List<int> GetLevels(IEnumerable<IConstraint> constraints)
    {
        return constraints.Select(c => c.Level).ToList();
    }

    int GetCount(IEnumerable<IConstraint> constraints)
    {
        return constraints.Count();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.False(result.GeneratedCode.Contains(".Count"),
            $"Generated code still has .Count:\n{result.GeneratedCode}");
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
