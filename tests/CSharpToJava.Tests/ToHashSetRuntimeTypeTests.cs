using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ ToHashSet() produces a CSharpHashSet (not a bare java.util.HashSet),
/// so casts/assignments to HashSet<T> (mapped to CSharpHashSet<T>) do not throw
/// ClassCastException at runtime.
/// </summary>
public class ToHashSetRuntimeTypeTests
{
    [Fact]
    public void ToHashSet_InsideSelectLambda_WrapsInCSharpHashSet()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<HashSet<int>> M(IEnumerable<Group> components) {
        return components.Select(g => g.Items.Select(x => x.Id).ToHashSet()).ToList();
    }
}
class Group { public IEnumerable<Item> Items { get; set; } }
class Item { public int Id { get; set; } }");
        Assert.True(result.Success, result.GeneratedCode);
        // The inner ToHashSet() is emitted via the stream path; it must produce a
        // CSharpHashSet so the enclosing Select result (HashSet<int> -> CSharpHashSet<Integer>)
        // does not get a java.util.HashSet. Wrapping the collect in new CSharpHashSet<>(...)
        // preserves the concrete C# HashSet type.
        Assert.Contains("new CSharpHashSet<>(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".collect(Collectors.toSet()))", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToHashSet_StandaloneAssignment_WrapsInCSharpHashSet()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class C {
    void M(IEnumerable<int> items) {
        HashSet<int> h = items.ToHashSet();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("new CSharpHashSet<>(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".collect(Collectors.toSet()))", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
