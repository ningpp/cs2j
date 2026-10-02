using System.Collections;
using System.Collections.Generic;

namespace SampleExpressionDesugarLib;

/// <summary>Single-argument Add collection carrying a constructor argument.</summary>
public sealed class AddBag : IEnumerable<int>
{
    private readonly List<int> _items = new();

    public AddBag()
    {
    }

    public AddBag(int capacity)
    {
        CtorArg = capacity;
    }

    public int CtorArg { get; }

    public string Contents => string.Join(",", _items);

    public void Add(int x) => _items.Add(x);

    public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Multi-argument (complex element) Add collection.</summary>
public sealed class PairsBag : IEnumerable
{
    private readonly List<string> _pairs = new();

    public string Contents => string.Join(";", _pairs);

    public void Add(int a, int b) => _pairs.Add(a + "-" + b);

    public IEnumerator GetEnumerator() => _pairs.GetEnumerator();
}

/// <summary>Fluent Add (returns the receiver; the value is discarded).</summary>
public sealed class FluentBag : IEnumerable
{
    private readonly List<int> _items = new();

    public string Contents => string.Join(",", _items);

    public FluentBag Add(int x)
    {
        _items.Add(x);
        return this;
    }

    public IEnumerator GetEnumerator() => _items.GetEnumerator();
}

/// <summary>params Add collection (one Add call per source element).</summary>
public sealed class ParamsBag : IEnumerable
{
    private readonly List<int> _items = new();

    public string Contents => string.Join(",", _items);

    public void Add(params int[] xs) => _items.AddRange(xs);

    public IEnumerator GetEnumerator() => _items.GetEnumerator();
}

/// <summary>Enumerable collection without an instance Add (extension Add only).</summary>
public sealed class GenBag : IEnumerable<int>
{
    private readonly List<int> _items = new();

    public string Contents => string.Join(",", _items);

    internal void AddCore(int x) => _items.Add(x);

    public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class GenBagExtensions
{
    public static void Add(this GenBag bag, int x) => bag.AddCore(x);
}

/// <summary>Collection/dictionary initializer behavior observations.</summary>
public static class CollectionScenarios
{
    public static string CtorArgPreserved()
    {
        var b = new AddBag(8) { 1, 2 };
        return b.CtorArg + ":" + b.Contents;
    }

    public static string MultiArg()
    {
        var p = new PairsBag { { 1, 2 }, { 3, 4 } };
        return p.Contents;
    }

    public static string Fluent()
    {
        var f = new FluentBag { 1, 2 };
        return f.Contents;
    }

    public static string Params()
    {
        var p = new ParamsBag { 1, 2, 3 };
        return p.Contents;
    }

    public static string Extension()
    {
        var g = new GenBag { 4, 5 };
        return g.Contents;
    }

    public static string DictionaryIndex()
    {
        var d = new Dictionary<int, string> { [1] = "a", [2] = "b" };
        return d[1] + d[2] + ":" + d.Count;
    }
}
