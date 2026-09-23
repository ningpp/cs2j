using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// AddRange is mapped to CSharpList.addRange() which accepts any Iterable,
/// so both ICollection and IEnumerable-only sources use addRange directly.
/// </summary>
public class AddRangeNonCollectionTests
{
    private readonly ITestOutputHelper _out;
    public AddRangeNonCollectionTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void AddRange_WithList_UsesAddRange()
    {
        var r = Convert(@"
using System.Collections.Generic;
class Segment { public int Id; }
class Sample {
    void Test() {
        var list = new List<Segment>();
        var other = new List<Segment>();
        list.AddRange(other);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // CSharpList.addRange handles both Collection and non-Collection sources
        Assert.Contains("list.addRange(other)", code);
    }
}
