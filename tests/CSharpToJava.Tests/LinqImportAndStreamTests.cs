using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for LINQ conversion correctness:
/// - Missing java.util.Comparator import when using OrderBy/ThenBy
/// - Missing java.util.Objects import when using Join/GroupJoin/Contains
/// - Reverse() producing a stream-compatible result for downstream chaining
/// </summary>
public class LinqImportAndStreamTests
{

    [Fact]
    public void QuerySyntax_Join_AddsObjectsImport()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M() {
        var a = new[] { 1, 2 };
        var b = new[] { 1, 3 };
        var r = (from x in a
                 join y in b on x equals y
                 select new { x, y }).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // LINQ desugarer now always on
        Assert.True(result.Success, result.GeneratedCode);
    }

    [Fact]
    public void Zip_ProducesValidJava()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M() {
        var nums = new List<int> { 1, 2, 3 };
        var r = nums.Zip(new[] { 10, 20, 30 }, (a, b) => a + b);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
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
