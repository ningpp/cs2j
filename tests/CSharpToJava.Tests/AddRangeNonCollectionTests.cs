using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// When AddRange is called with an IEnumerable source that is NOT ICollection
/// (e.g. a custom type implementing only IEnumerable&lt;T&gt;), the converter
/// must use forEach(list::add) instead of addAll() which requires Collection.
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
        // Should use forEach instead of addAll since Path is not Collection
        Assert.Contains("forEach(list::add)", code);
        Assert.DoesNotContain("addAll", code);
    }

    [Fact]
    public void AddRange_WithList_UsesAddAll()
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
        // List implements ICollection, should keep addAll
        Assert.Contains("addAll", code);
    }

    [Fact]
    public void AddRange_WithStringSplitArray_WrapsArrayForAddAll()
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
        Assert.Contains("holder.Values.addAll(ArrayHelper.toList(text.split(\" \")))", code);
        Assert.DoesNotContain("holder.Values.addAll(text.split(\" \"))", code);
    }

    [Fact]
    public void AddRange_WithIEnumerableInterface_UsesForEach()
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
        // IEnumerable LINQ chain → Stream in Java, not Collection → use forEach
        Assert.Contains("forEach(target::add)", code);
        Assert.DoesNotContain("addAll", code);
    }
}
