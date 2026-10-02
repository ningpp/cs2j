using System;

namespace SampleForeachSourceLib;

// =====================================================================
// C40 foreach-source M3 fixture: ref / ref-readonly foreach sources.
// FR-2 Span<T>/ReadOnlySpan<T> and a span-like custom ref struct
// (Count + ref indexer, plus a ref-Current enumerator so the source is
// legal C#), FR-3 an enumerator whose Current returns by ref.
// Indexers/Current use statement-bodied accessors so later rewriters
// (C01) never see expression-bodied ref members.
//
// FR-1 rank-1 array ref foreach is intentionally absent here: csc
// (Roslyn 5) rejects that shape with CS1510 even when the array is a
// local, so no compilable input can contain it. The pass still lowers
// it defensively; coverage lives in ForeachSourceC40RewriterUnitTests
// (lowered output is emitted and executed).
// =====================================================================

/// <summary>
/// FR-2 custom source: span-like — public Count + single-int ref indexer
/// plus a by-ref-Current GetEnumerator (the latter is what makes the
/// ref foreach legal C#; the pass prefers the index path).
/// </summary>
public ref struct CountIndexBag
{
    private readonly int[] _values;

    public CountIndexBag(int[] values)
    {
        _values = values;
    }

    public int Count
    {
        get { return _values.Length; }
    }

    public ref int this[int index]
    {
        get { return ref _values[index]; }
    }

    public Enumerator GetEnumerator()
    {
        return new Enumerator(_values);
    }

    public struct Enumerator
    {
        private readonly int[] _values;
        private int _index;

        public Enumerator(int[] values)
        {
            _values = values;
            _index = -1;
        }

        public ref int Current
        {
            get { return ref _values[_index]; }
        }

        public bool MoveNext()
        {
            _index++;
            return _index < _values.Length;
        }
    }
}

/// <summary>FR-3 enumerator: value type, ref-returning Current, no Dispose.</summary>
public struct RefReadEnumerator
{
    private readonly int[] _values;
    private int _index;

    public RefReadEnumerator(int[] values)
    {
        _values = values;
        _index = -1;
    }

    public ref int Current
    {
        get { return ref _values[_index]; }
    }

    public bool MoveNext()
    {
        _index++;
        return _index < _values.Length;
    }
}

/// <summary>FR-3 source: GetEnumerator returns the ref-Current struct.</summary>
public sealed class RefReadCollection
{
    private readonly int[] _values;

    public RefReadCollection(int[] values)
    {
        _values = values;
    }

    public RefReadEnumerator GetEnumerator()
    {
        return new RefReadEnumerator(_values);
    }
}

public static class RefCollections
{
    /// <summary>FR-2 writable Span: ref item doubles every element in place.</summary>
    public static void DoubleSpan(Span<int> values)
    {
        foreach (ref var value in values)
        {
            value *= 2;
        }
    }

    /// <summary>FR-2 ReadOnlySpan: ref-readonly item sums without copying.</summary>
    public static int SumReadOnlySpanRef(ReadOnlySpan<int> values)
    {
        var sum = 0;
        foreach (ref readonly var value in values)
        {
            sum += value;
        }

        return sum;
    }

    /// <summary>FR-2 custom Count+ref-indexer ref struct doubles in place.</summary>
    public static void DoubleCountIndexBag(CountIndexBag values)
    {
        foreach (ref var value in values)
        {
            value *= 2;
        }
    }

    /// <summary>FR-3 ref-Current enumerator doubles every element in place.</summary>
    public static void DoubleRefReadCollection(RefReadCollection values)
    {
        foreach (ref var value in values)
        {
            value *= 2;
        }
    }
}
