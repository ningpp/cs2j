using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace SampleExpressionDesugarLib;

/// <summary>Builder target whose factory accepts a ReadOnlySpan&lt;int&gt;.</summary>
[CollectionBuilder(typeof(MyBuiltBuilder), "Create")]
public sealed class MyBuilt : IEnumerable<int>
{
    private readonly int[] _items;

    public MyBuilt(int[] items) => _items = items;

    public int Count
    {
        get { return _items.Length; }
    }

    public IEnumerator<int> GetEnumerator()
        => ((IEnumerable<int>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

public static class MyBuiltBuilder
{
    public static MyBuilt Create(ReadOnlySpan<int> items)
        => new(items.ToArray());
}

/// <summary>Builder target over KeyValuePair elements (non-special element type).</summary>
[CollectionBuilder(typeof(MyMapBuilder), "Create")]
public sealed class MyMap : IEnumerable<KeyValuePair<string, int>>
{
    private readonly KeyValuePair<string, int>[] _items;

    public MyMap(KeyValuePair<string, int>[] items) => _items = items;

    public int Count
    {
        get { return _items.Length; }
    }

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, int>>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

public static class MyMapBuilder
{
    public static MyMap Create(ReadOnlySpan<KeyValuePair<string, int>> items)
        => new(items.ToArray());
}

/// <summary>CE-10 factory emission sites (no-spread, empty and spread forms).</summary>
public static class BuilderSites
{
    public static MyBuilt NoSpread() => [1, 2, 3];

    public static MyBuilt Empty() => [];

    public static MyBuilt WithSpread(int[] prefix, int last) => [.. prefix, last];

    public static MyMap MapLiteral()
        => [new KeyValuePair<string, int>("a", 1), new KeyValuePair<string, int>("b", 2)];

    public static ImmutableArray<int> Immutable() => [1, 2, 3];

    public static ImmutableArray<int> ImmutableEmpty() => [];
}
