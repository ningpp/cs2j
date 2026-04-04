using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that IList implementation's void Add() gets return true appended
/// even when using structured body (IR) instead of raw Body string.
/// </summary>
public class ListAddReturnTrueTests
{
    private readonly ITestOutputHelper _out;
    public ListAddReturnTrueTests(ITestOutputHelper output) { _out = output; }

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
    public void IListAdd_VoidToBoolean_ReturnsTrue()
    {
        var r = Convert(@"
using System.Collections;
using System.Collections.Generic;
class NodeCollection : IList<string> {
    private List<string> items = new List<string>();
    public string this[int index] { get => items[index]; set => items[index] = value; }
    public int Count => items.Count;
    public bool IsReadOnly => false;
    public void Add(string item) { items.Add(item); }
    public void Clear() { items.Clear(); }
    public bool Contains(string item) { return items.Contains(item); }
    public void CopyTo(string[] array, int index) {}
    public IEnumerator<string> GetEnumerator() { return items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator() { return items.GetEnumerator(); }
    public int IndexOf(string item) { return items.IndexOf(item); }
    public void Insert(int index, string item) { items.Insert(index, item); }
    public bool Remove(string item) { return items.Remove(item); }
    public void RemoveAt(int index) { items.RemoveAt(index); }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // add() must return boolean with a return true statement
        Assert.Contains("boolean add(", code);
        Assert.Contains("return true;", code);
        Assert.DoesNotContain("void add(", code);
    }
}
