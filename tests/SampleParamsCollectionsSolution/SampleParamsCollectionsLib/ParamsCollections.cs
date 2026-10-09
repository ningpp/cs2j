using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace SampleParamsCollectionsLib;

/// <summary>
/// C48 fixture API surface (design §7.2): the C# 13 params-collection kinds
/// that the strip-default pass materializes (K2/K4/K5) next to classic array
/// params (K1, kept for the Java varargs mapping). Every member is written so
/// the very same source behaves identically before preprocessing and on the
/// preprocessed intermediate tree — ParamsBehaviorTests runs on both.
/// </summary>
public static class ParamsLib
{
    /// <summary>Empty-state identity probe: captures the collection received.</summary>
    public static int[]? LastArray;
    public static IEnumerable<int>? LastEnumerable;
    public static List<int>? LastList;

    private static readonly List<int> TraceLog = new();

    public static IReadOnlyList<int> Trace => TraceLog;

    public static void ResetTrace() => TraceLog.Clear();

    public static int TraceValue(int value)
    {
        TraceLog.Add(value);
        return value;
    }

    // K1 — classic params T[]: the declaration keeps its params modifier
    // (strategy A), and with the default rollout its comma call sites stay
    // comma-separated. The empty lowering is the compiler's Array.Empty
    // singleton — observable through two empty calls below.
    public static int Sum(params int[] values)
    {
        LastArray = values;
        var total = 0;
        foreach (var v in values)
        {
            total += v;
        }

        return total;
    }

    // K1 generic — same strategy A treatment.
    public static string JoinAll<T>(params T[] items) => string.Join(",", items);

    // K2 — a ReadOnlySpan<int> params collection: materialized into a plain
    // parameter; its comma/empty call sites are explicitized as arrays
    // (implicit T[] → ReadOnlySpan<T> conversion). Writable through the span.
    public static string FormatInts(params ReadOnlySpan<int> values)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('|');
            }

            sb.Append(values[i]);
        }

        return sb.ToString();
    }

    public static void ZeroFirst(params Span<int> xs) => xs[0] = 0;

    public static int[] CopyAndZero(params Span<int> xs)
    {
        xs[0] = 0;
        return xs.ToArray();
    }

    // K5 — an IReadOnlyList<int> params collection: materialized; comma call
    // sites become new List<T>(){…}, empty call sites become
    // System.Array.Empty<T>().
    public static int SumAll(params IReadOnlyList<int> values)
    {
        LastEnumerable = values;
        var total = 0;
        for (var i = 0; i < values.Count; i++)
        {
            total += values[i];
        }

        return total;
    }

    // K4 — a List<int> params collection: materialized; empty call sites
    // become a fresh new List<int>() per call (never a singleton).
    public static List<int> Batches(params List<int> items)
    {
        LastList = items;
        return items;
    }

    /// <summary>Form-B identity probe: the callee mutates the caller's list.</summary>
    public static void MutateBatch(params List<int> items) => items.Add(99);

    // ------------------------------------------------------------------
    // K3 (M2, PC-9) — [CollectionBuilder]-backed params collections:
    // custom factory, generic arity, ReadOnlySpan<KeyValuePair<,>> factory.
    // Under the Emit policy the strip pass rewrites comma sites to
    // <Builder>.Create(new T[]{…}) and empty sites to <Builder>.Create(new T[]{});
    // the very same calls compile natively on the unprocessed source.
    // ------------------------------------------------------------------

    public static ParamsBag? LastBag;

    public static int BagCount(params ParamsBag bag)
    {
        LastBag = bag;
        return bag.Count;
    }

    /// <summary>PC-7 forwarding overload: the empty params slot is filled with the builder empty state.</summary>
    public static int BagForwarded() => BagCount();

    public static int ImmCount<T>(params ParamsImm<T> items) => items.Count;

    public static int MapCount(params ParamsMap map) => map.Count;

    // D7 generic tiers: List / ReadOnlySpan / builder-backed across the
    // int / struct / reference element tiers.
    public static int TierList<T>(params List<T> items) => items.Count;

    public static int TierSpan<T>(params ReadOnlySpan<T> items) => items.Length;

    public static int TierImm<T>(params ParamsImm<T> items) => items.Count;
}

/// <summary>Struct tier probe for the generic int/struct/reference matrix.</summary>
public struct TierPoint
{
    public int X { get; set; }
    public int Y { get; set; }
}

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
