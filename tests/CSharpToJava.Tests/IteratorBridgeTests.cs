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
    /// C# IEnumerator with explicit interface impl maps to CSharpEnumerator<T>.
    /// The converted type must also expose Java Iterator-compatible hasNext()/next().
    /// </summary>
    [Fact]
    public void ExplicitIEnumerator_ImplementsCSharpEnumeratorAndJavaIteratorBridge()
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

        Assert.Contains("implements CSharpEnumerator<Integer>", code);
        Assert.Contains("public boolean moveNext()", code);
        Assert.Contains("public Integer getCurrent()", code);
        Assert.Contains("_iteratorHasNext", code);
        Assert.Contains("public boolean hasNext()", code);
        Assert.Contains("public Integer next()", code);
        Assert.Contains("_iteratorHasNext = false;", code);
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

        // hasNext must cache the MoveNext result and be safe to call repeatedly.
        Assert.Contains("if (_iteratorHasNext) return true;", code);
        Assert.Contains("_iteratorHasNext = moveNext()", code);
        Assert.Contains("public String next()", code);
    }

    /// <summary>
    /// C# IEnumerator.Current is a stable read after MoveNext(); Java Iterator.next()
    /// advances, so explicit GetEnumerator/MoveNext/Current code must use a C#-style
    /// enumerator adapter instead of translating Current to next().
    /// </summary>
    [Fact]
    public void ExplicitGetEnumerator_CurrentDoesNotAdvance()
    {
        var r = Convert(@"
using System.Collections.Generic;

class Walker
{
    public int Sum(IEnumerable<int> values)
    {
        var en = values.GetEnumerator();
        en.MoveNext();
        var first = en.Current;
        var again = en.Current;
        return first + again;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        Assert.Contains("CSharpEnumerator.from(values.iterator())", code);
        Assert.Contains("en.moveNext()", code);
        Assert.Contains("en.getCurrent()", code);
        Assert.DoesNotContain("en.next()", code);
    }

    [Fact]
    public void ExplicitIEnumeratorLocalWithoutInitializer_UsesCSharpEnumeratorType()
    {
        var r = Convert(@"
using System.Collections.Generic;

class Walker
{
    public int First(IEnumerable<int> values)
    {
        IEnumerator<int> en;
        en = values.GetEnumerator();
        en.MoveNext();
        return en.Current;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        Assert.Contains("CSharpEnumerator<Integer> en;", code);
        Assert.Contains("en = CSharpEnumerator.from(values.iterator())", code);
        Assert.Contains("en.moveNext()", code);
        Assert.Contains("en.getCurrent()", code);
        Assert.DoesNotContain("Iterator<Integer> en;", code);
    }

    [Fact]
    public void ExplicitNonGenericIEnumeratorLocalInitializedToNull_UsesCSharpEnumeratorType()
    {
        var r = Convert(@"
using System.Collections;

class Walker
{
    public int First(IEnumerable values)
    {
        IEnumerator en = null;
        if (values != null)
        {
            en = values.GetEnumerator();
        }
        return en != null && en.MoveNext() ? (int)en.Current : 0;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        Assert.Contains("CSharpEnumerator en = null;", code);
        Assert.DoesNotContain("var en = null;", code);
        Assert.Contains("en = CSharpEnumerator.from(values.iterator())", code);
        Assert.Contains("en.moveNext()", code);
        Assert.Contains("en.getCurrent()", code);
    }

    [Fact]
    public void ExplicitIEnumeratorParameter_UsesCSharpEnumeratorType()
    {
        var r = Convert(@"
using System.Collections.Generic;

class Walker
{
    public int First(IEnumerator<int> en)
    {
        return en.MoveNext() ? en.Current : 0;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        Assert.Contains("int first(CSharpEnumerator<Integer> en)", code);
        Assert.Contains("en.moveNext()", code);
        Assert.Contains("en.getCurrent()", code);
        Assert.DoesNotContain("Iterator<Integer> en", code);
    }

    [Fact]
    public void ExplicitIEnumeratorFieldAndConstructor_UseCSharpEnumeratorType()
    {
        var r = Convert(@"
using System.Collections.Generic;

class Walker
{
    private IEnumerator<int> en;

    public Walker(IEnumerator<int> en)
    {
        this.en = en;
    }

    public int First()
    {
        en.MoveNext();
        return en.Current;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        Assert.Contains("private CSharpEnumerator<Integer> en;", code);
        Assert.Contains("public Walker(CSharpEnumerator<Integer> en)", code);
        Assert.Contains("this.en = en;", code);
        Assert.Contains("en.moveNext()", code);
        Assert.Contains("en.getCurrent()", code);
        Assert.DoesNotContain("Iterator<Integer> en", code);
    }

    [Fact]
    public void YieldIteratorMethodOnEnumerable_UsesIterableElementType()
    {
        var r = Convert(@"
using System.Collections;
using System.Collections.Generic;

class Numbers : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator()
    {
        yield return 1;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        Assert.Contains("implements Iterable<Integer>", code);
        Assert.Contains("public CSharpEnumerator<Integer> iterator()", code);
        Assert.Contains("ArrayList<Integer> _yieldResult", code);
        Assert.Contains("return CSharpEnumerator.from(_yieldResult.iterator())", code);
        Assert.DoesNotContain("Iterator<Object> iterator()", code);
    }

    [Fact]
    public void PatternGetEnumeratorClass_StillImplementsIterable()
    {
        var r = Convert(@"
using System.Collections;
using System.Collections.Generic;

class SuccEnumerator : IEnumerator<int>
{
    public bool MoveNext() { return false; }
    public int Current { get { return 0; } }
    object IEnumerator.Current { get { return Current; } }
    public void Reset() { }
    public void Dispose() { }
}

class Succ
{
    public IEnumerator<int> GetEnumerator()
    {
        return new SuccEnumerator();
    }
}

class Walker
{
    public int Sum()
    {
        var sum = 0;
        foreach (int v in new Succ())
            sum += v;
        return sum;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        Assert.Contains("class Succ implements Iterable<Integer>", code);
        Assert.Contains("public CSharpEnumerator<Integer> iterator()", code);
        Assert.Contains("for (int v : new Succ())", code);
    }

    [Fact]
    public void OverrideNonGenericEnumeratorFromEnumerableBase_DoesNotReimplementIterable()
    {
        var r = Convert(@"
using System.Collections;

abstract class NamespaceManager : IEnumerable
{
    public abstract IEnumerator GetEnumerator();
}

class NoNamespaceManager : NamespaceManager
{
    public override IEnumerator GetEnumerator()
    {
        return null;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        Assert.Contains("abstract class NamespaceManager implements Iterable", code);
        Assert.Contains("class NoNamespaceManager extends NamespaceManager", code);
        Assert.DoesNotContain("class NoNamespaceManager extends NamespaceManager implements Iterable<Object>", code);
    }
}
