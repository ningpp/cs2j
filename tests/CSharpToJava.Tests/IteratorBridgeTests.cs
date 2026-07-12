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

        Assert.Contains("implements CSharpGenericEnumerator<Integer>", code);
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

        Assert.Contains("CSharpGenericEnumerator.from(values.iterator())", code);
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

        Assert.Contains("CSharpGenericEnumerator<Integer> en;", code);
        Assert.Contains("en = CSharpGenericEnumerator.from(values.iterator())", code);
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

        Assert.Contains("int first(CSharpGenericEnumerator<Integer> en)", code);
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

        Assert.Contains("private CSharpGenericEnumerator<Integer> en;", code);
        Assert.Contains("public Walker(CSharpGenericEnumerator<Integer> en)", code);
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

        Assert.Contains("CSharpGenericIterable<Integer>", code);
        Assert.Contains("Iterable<Integer>", code);
        Assert.Contains("public CSharpGenericEnumerator<Integer> iterator()", code);
        Assert.Contains("CSharpList<Integer> _yieldResult", code);
        Assert.Contains("return _yieldResult.iterator()", code);
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
        Assert.Contains("public CSharpGenericEnumerator<Integer> iterator()", code);
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

        Assert.Contains("abstract class NamespaceManager implements CSharpGenericIterable<Object>, Iterable<Object>", code);
        Assert.Contains("class NoNamespaceManager extends NamespaceManager", code);
        Assert.DoesNotContain("class NoNamespaceManager extends NamespaceManager implements Iterable<Object>", code);
    }

    /// <summary>
    /// Verifies that a C# class implementing non-generic IEnumerator with explicit interface implementations
    /// converts to a Java class implementing CSharpEnumerator (not CSharpGenericEnumerator&lt;?&gt;).
    /// This is the specific scenario from the bug report: EmptyEnumerator implementing IEnumerator
    /// with explicit interface implementations for MoveNext(), Reset(), and Current.
    /// </summary>
    [Fact]
    public void NonGenericIEnumeratorWithExplicitInterfaceImpl_MapsToCSharpEnumerator()
    {
        var r = Convert(@"
using System;
using System.Collections;

namespace dotnet.xml
{
    internal sealed class EmptyEnumerator : IEnumerator
    {
        bool IEnumerator.MoveNext()
        {
            return false;
        }

        void IEnumerator.Reset()
        {
        }

        object IEnumerator.Current
        {
            get
            {
                throw new InvalidOperationException(""SR.Xml_InvalidOperation"");
            }
        }
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        // Must implement CSharpEnumerator (non-generic), NOT CSharpGenericEnumerator<?>
        Assert.Contains("implements CSharpEnumerator", code);
        Assert.DoesNotContain("CSharpGenericEnumerator", code);

        // Bridge methods must use valid Java types (not wildcard ?)
        Assert.Contains("public Object next()", code);
        Assert.DoesNotContain("public ? next()", code);

        // Must have proper iterator bridge methods
        Assert.Contains("public boolean hasNext()", code);
        Assert.Contains("public boolean moveNext()", code);
        Assert.Contains("public Object getCurrent()", code);
        Assert.Contains("public void reset()", code);

        // Must import CSharpEnumerator
        Assert.Contains("import io.github.ningpp.compat.CSharpEnumerator;", code);
    }

    /// <summary>
    /// Verifies that a C# class implementing non-simple IEnumerator (non-explicit interface impl)
    /// also maps to CSharpEnumerator correctly.
    /// </summary>
    [Fact]
    public void NonGenericIEnumeratorSimpleImpl_MapsToCSharpEnumerator()
    {
        var r = Convert(@"
using System.Collections;

class SimpleEmptyEnumerator : IEnumerator
{
    public bool MoveNext() { return false; }
    public void Reset() { }
    public object Current { get { return null; } }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        // Must implement CSharpEnumerator (non-generic)
        Assert.Contains("implements CSharpEnumerator", code);
        Assert.DoesNotContain("CSharpGenericEnumerator", code);

        // Bridge methods must use valid Java types
        Assert.DoesNotContain("public ? next()", code);
        Assert.Contains("public Object next()", code);
    }

    /// <summary>
    /// Verifies that non-generic IEnumerator field/variable types map to CSharpEnumerator.
    /// </summary>
    [Fact]
    public void NonGenericIEnumeratorVariableType_MapsToCSharpEnumerator()
    {
        var r = Convert(@"
using System.Collections;

class Walker
{
    public int First(IEnumerable values)
    {
        IEnumerator en = values.GetEnumerator();
        if (en.MoveNext())
            return (int)en.Current;
        return 0;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        // Variable type must be CSharpEnumerator (non-generic)
        Assert.Contains("CSharpEnumerator en =", code);
        Assert.DoesNotContain("CSharpGenericEnumerator<?> en =", code);

        // Must use moveNext() and getCurrent() (CSharpEnumerator pattern)
        Assert.Contains("en.moveNext()", code);
        Assert.Contains("en.getCurrent()", code);
    }

    [Fact]
    public void NonGenericIEnumerable_ImplementsClause_UsesObjectNotWildcard()
    {
        var r = Convert(@"
using System.Collections;

class MyCollection : IEnumerable
{
    public IEnumerator GetEnumerator() { return null; }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        // Must NOT use wildcard ? in implements clause — Java forbids it
        Assert.DoesNotContain("CSharpGenericIterable<?>", code);
        // Must use CSharpGenericIterable<Object> instead
        Assert.Contains("CSharpGenericIterable<Object>", code);
    }

    /// <summary>
    /// When a non-generic IEnumerator-returning method calls GetEnumerator() on a
    /// collection whose GetEnumerator returns a type implementing IEnumerator&lt;T&gt;,
    /// the converter must use CSharpEnumerator.from() (not CSharpGenericEnumerator.from())
    /// because CSharpGenericEnumerator&lt;T&gt; is not a subtype of CSharpEnumerator.
    /// </summary>
    [Fact]
    public void NonGenericEnumeratorReturn_CallsGenericGetEnumerator_UsesCSharpEnumeratorFrom()
    {
        var r = Convert(@"
using System.Collections;
using System.Collections.Generic;

class MyNamespaceManager : IEnumerable
{
    public IEnumerator GetEnumerator()
    {
        Dictionary<string, string> prefixes = new Dictionary<string, string>();
        return prefixes.Keys.GetEnumerator();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";

        // Must use CSharpEnumerator.from() because the method returns IEnumerator (non-generic)
        Assert.Contains("CSharpEnumerator.from(", code);
        Assert.DoesNotContain("CSharpGenericEnumerator.from(", code);
    }
}
