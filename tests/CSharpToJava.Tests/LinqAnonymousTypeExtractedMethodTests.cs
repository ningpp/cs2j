using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ queries with anonymous type select, extracted to a helper method
/// by the LINQ rewriter (because the foreach body contains control flow like continue/break),
/// correctly synthesize the record type in the method signature and foreach variable.
///
/// Root cause: When the foreach body has continue/break/return, CanRewrapForeachVisitor fails
/// and the LINQ expression is extracted to a separate yield-return method.  The rewriter
/// sets the anonymous type to null (SelectMethod handler), producing a broken return type
/// that becomes List&lt;T&gt; (with T undefined) in Java. The foreach variable degrades to Object.
/// </summary>
public class LinqAnonymousTypeExtractedMethodTests
{
    /// <summary>
    /// Reproduces the MsmtRectilinearPath.java compilation error:
    /// LINQ query with anonymous type extracted to helper method,
    /// foreach body contains 'continue', forcing method extraction.
    /// The helper method return type must use the synthesized record, not 'T'.
    /// The foreach variable must be the synthesized record, not 'Object'.
    /// </summary>
    [Fact]
    public void ExtractedLinqMethod_WithAnonymousType_ShouldNotHaveUndefinedT()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Vertex { public double X { get; set; } public double Y { get; set; } }

class PathFinder
{
    void FindPath(IEnumerable<Vertex> sources, IEnumerable<Vertex> targets)
    {
        foreach (var pair in
            from Vertex source in sources
            from Vertex target in targets
            orderby Math.Abs(source.X - target.X) + Math.Abs(source.Y - target.Y)
            select new { sourceV = source, targetV = target })
        {
            if (pair.sourceV.X == pair.targetV.X)
                continue;
            var s = pair.sourceV;
            var t = pair.targetV;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;

        // The helper method must NOT have undefined type parameter T
        Assert.DoesNotContain("List<T>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ArrayList<T>", code, StringComparison.Ordinal);

        // The foreach variable must NOT be typed as Object
        Assert.DoesNotContain("Object pair", code, StringComparison.Ordinal);

        // Member access must work — .sourceV and .targetV should appear
        // as either field access (pair.sourceV) or getter call (pair.sourceV())
        Assert.Contains("sourceV", code, StringComparison.Ordinal);
        Assert.Contains("targetV", code, StringComparison.Ordinal);

        // The synthesized record should be present
        Assert.Contains("record", code, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Method chain variant: items.Cast().SelectMany(...).Select(x => new { ... })
    /// with foreach body containing 'continue', forcing extraction.
    /// </summary>
    [Fact]
    public void ExtractedLinqMethod_MethodChain_WithAnonymousType_ShouldNotHaveUndefinedT()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Item { public string Name { get; set; } public int Value { get; set; } }

class Processor
{
    void Run(IEnumerable<Item> items, IEnumerable<Item> others)
    {
        foreach (var x in items
            .Cast<Item>()
            .SelectMany(i => others.Cast<Item>()
                .Select(j => new { left = i, right = j })))
        {
            if (x.left.Value < 0)
                continue;
            var n = x.left;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;

        // No undefined T
        Assert.DoesNotContain("List<T>", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ArrayList<T>", code, StringComparison.Ordinal);

        // No Object typing for the foreach variable
        Assert.DoesNotContain("Object x", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions { PreferStreamApi = false },
        });
    }
}
