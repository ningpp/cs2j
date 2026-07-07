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
    public void AddRange_WithIEnumerableOnly_UsesForEach()
    {
        var r = Convert(@"
using System.Collections;
using System.Collections.Generic;
class Segment { public int Id; }
class Path : IEnumerable<Segment> {
    public IEnumerator<Segment> GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => null;
}
class Sample {
    void Test() {
        var list = new List<Segment>();
        var path = new Path();
        list.AddRange(path);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // IEnumerable-only sources use forEach since addRange requires Collection
        Assert.Contains("path.forEach(list::add)", code);
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

    [Fact]
    public void AddRange_WithStringSplitArray_UsesAddRangeDirectly()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Xml;
class Holder {
    public List<string> Values = new List<string>();
}
class Sample {
    XmlReader reader;
    void Test() {
        var text = reader.GetAttribute(""ids"");
        var holder = new Holder();
        holder.Values.AddRange(text.Split(' '));
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var code = r.GeneratedCode ?? "";
        // CSharpList.addRange accepts arrays directly (via Iterable bridge)
        Assert.Contains("holder.Values.addRange(text.split(\" \"))", code);
    }

    [Fact]
    public void AddRange_WithIEnumerableInterface_UsesAddRange()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Node { public int Id; public IEnumerable<Node> Children; }
class Sample {
    void Test(IEnumerable<Node> source, List<Node> target) {
        target.AddRange(source.Where(n => n.Id > 0));
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // LINQ results (IEnumerable-only) use forEach since addRange requires Collection
        Assert.Contains(".forEach(target::add)", code);
    }
}
