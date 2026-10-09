using System.Collections;
using System.Runtime.CompilerServices;

namespace SampleStripDefaultLib;

// C48 M2 (design §7.2): params-collections fixture surface for the
// SampleStripDefaultSolution. Covers K2 Span, K4 initializable, K5 interfaces,
// K3 [CollectionBuilder] (custom / generic arity / KeyValuePair span),
// E08 default + K3 combinations and the generic int/struct/reference tiers.

[CollectionBuilder(typeof(ParamsBagBuilder), "Create")]
public sealed class ParamsBag : IEnumerable<int>
{
    private readonly int[] _items;

    public ParamsBag(int[] items)
    {
        _items = items;
    }

    public int Count => _items.Length;
    public int this[int index] => _items[index];

    public IEnumerator<int> GetEnumerator()
    {
        foreach (var i in _items)
        {
            yield return i;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class ParamsBagBuilder
{
    public static ParamsBag Create(ReadOnlySpan<int> items) => new(items.ToArray());
}

[CollectionBuilder(typeof(ParamsImmBuilder), "Create")]
public sealed class ParamsImm<T> : IEnumerable<T>
{
    private readonly T[] _items;

    public ParamsImm(T[] items)
    {
        _items = items;
    }

    public int Count => _items.Length;

    public IEnumerator<T> GetEnumerator()
    {
        foreach (var i in _items)
        {
            yield return i;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class ParamsImmBuilder
{
    public static ParamsImm<T> Create<T>(ReadOnlySpan<T> items) => new(items.ToArray());
}

[CollectionBuilder(typeof(ParamsMapBuilder), "Create")]
public sealed class ParamsMap : IEnumerable<KeyValuePair<string, int>>
{
    private readonly KeyValuePair<string, int>[] _entries;

    public ParamsMap(KeyValuePair<string, int>[] entries)
    {
        _entries = entries;
    }

    public int Count => _entries.Length;

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator()
    {
        foreach (var entry in _entries)
        {
            yield return entry;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class ParamsMapBuilder
{
    public static ParamsMap Create(ReadOnlySpan<KeyValuePair<string, int>> entries) => new(entries.ToArray());
}

public struct ParamsPoint
{
    public int X { get; set; }
    public int Y { get; set; }
}

public static class ParamsApi
{
    // K2: Span/ReadOnlySpan shapes.
    public static int SumRos(params ReadOnlySpan<int> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total += value;
        }

        return total;
    }

    public static string FormatRos(char separator, params ReadOnlySpan<string> values)
        => string.Join(separator, values.ToArray());

    // K4: concrete collection-initializer type.
    public static int BatchCount(params List<int> items) => items.Count;

    // K5: one of the five legal interfaces.
    public static int CountAll(params IEnumerable<int> items)
    {
        var count = 0;
        foreach (var _ in items)
        {
            count++;
        }

        return count;
    }

    // K3: builder-backed types.
    public static int BagCount(params ParamsBag bag) => bag.Count;

    public static int ImmCount<T>(params ParamsImm<T> items) => items.Count;

    public static int MapCount(params ParamsMap map) => map.Count;

    // E08 defaulted fixed parameter followed by a K3 params slot.
    public static int BagWithSeed(int seed = 1, params ParamsBag bag) => seed + bag.Count;

    // D7 generic tiers: the same API across int / struct / reference types.
    public static int TierList<T>(params List<T> items) => items.Count;

    public static int TierSpan<T>(params ReadOnlySpan<T> items) => items.Length;

    public static int TierImm<T>(params ParamsImm<T> items) => items.Count;
}
