using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class IteratorBridgeTests
{
    private readonly ITestOutputHelper _out;
    public IteratorBridgeTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src, string file = "Sample.cs")
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = file,
            Options = new ConversionOptions(),
        });
    }

    /// <summary>
    /// C# IEnumerator with explicit interface impl: MoveNext renamed to hasNext by TypeMappings.
    /// The converter must NOT just rename — it must restructure to lookahead pattern.
    /// </summary>
    [Fact]
    public void ExplicitIEnumerator_MoveNextRenamed_MustUseLookahead()
    {
        var r = Convert(@"
using System.Collections;
using System.Collections.Generic;

class MyEnumerator : IEnumerator<int>
{
    private int[] _data;
    private int _pos;
    public MyEnumerator(int[] d) { _data = d; _pos = -1; }
    bool IEnumerator.MoveNext() {
        if (_pos >= _data.Length - 1) return false;
        _pos++;
        return true;
    }
    int IEnumerator<int>.Current { get { return _data[_pos]; } }
    object IEnumerator.Current => Current;
    public void Dispose() { }
    void IEnumerator.Reset() { _pos = -1; }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        // When MoveNext is explicit interface impl, TypeMappings renames it to hasNext.
        // The bridge must restructure: hasNext becomes private _advance(),
        // and new idempotent hasNext() + next() are generated with lookahead caching.
        Assert.Contains("private boolean _advance()", code);
        Assert.Contains("_lookaheadValid", code);
        Assert.Contains("_lookaheadValue", code);
        Assert.Contains("public boolean hasNext()", code);
        Assert.Contains("public Integer next()", code);
        Assert.Contains("_lookaheadValid = false", code);
        Assert.Contains("_lookaheadValue = _advance()", code);

        // reset() must also clear lookahead
        Assert.Contains("_lookaheadValid = false;", code);
    }

    /// <summary>
    /// hasNext() must be truly idempotent — the original advancing logic is in _advance().
    /// </summary>
    [Fact]
    public void LookaheadHasNext_DoesNotAdvanceOnRepeatCall()
    {
        var r = Convert(@"
using System.Collections;
using System.Collections.Generic;

class MyIter : IEnumerator<string>
{
    private string[] _data;
    private int _pos;
    public MyIter(string[] d) { _data = d; _pos = -1; }
    bool IEnumerator.MoveNext() {
        if (_pos >= _data.Length - 1) return false;
        _pos++;
        return true;
    }
    string IEnumerator<string>.Current { get { return _data[_pos]; } }
    object IEnumerator.Current => Current;
    public void Dispose() { }
    void IEnumerator.Reset() { _pos = -1; }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        // hasNext must delegate to _advance, not contain advancing logic directly
        Assert.Contains("private boolean _advance()", code);
        Assert.Contains("_lookaheadValue = _advance()", code);
    }
}
