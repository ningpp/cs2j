using System.Collections;
using System.Collections.Generic;

namespace SampleExpressionDesugarLib;

/// <summary>
/// Custom mutable collection with a single-parameter <c>Add</c> (T3 bucket):
/// collection expressions targeting it zero-temp into a list initializer.
/// </summary>
public sealed class Bag<T> : IEnumerable<T>
{
    private readonly List<T> _items = new();

    public int Count
    {
        get { return _items.Count; }
    }

    public void Add(T item) => _items.Add(item);

    public void AddRange(IEnumerable<T> items) => _items.AddRange(items);

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>T3 concrete mutable targets, spreads and sibling nesting.</summary>
public static class MutableTargets
{
    public static List<int> ListLiteral(int extra)
        => [1, 2, extra];

    public static List<int> ListWithSpread(IReadOnlyList<int> prefix, int tail)
        => [.. prefix, tail, tail + 1];

    public static Bag<string> CustomAddBag()
        => ["a", "b", "c"];

    public static int SumGroups(IEnumerable<IEnumerable<int>> groups)
    {
        var sum = 0;
        foreach (var group in groups)
        {
            foreach (var value in group)
            {
                sum += value;
            }
        }

        return sum;
    }

    public static List<List<int>> NestedLists(IReadOnlyList<int> inner)
        => [[1], [.. inner, 2], [3, 4]];

    public static List<int[]> Matrix()
        => [[1, 2], [3, 4]];
}
