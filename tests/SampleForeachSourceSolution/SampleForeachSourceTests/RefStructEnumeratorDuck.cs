using System;
using System.Collections.Generic;

namespace SampleForeachSourceLib;

// =====================================================================
// C40 M2 ref-struct fixtures (design §M2): ref-like enumerator with an
// accessible parameterless void Dispose() is invoked directly by foreach
// WITHOUT boxing (Roslyn 5.0 probe-pinned). These live in the TEST
// assembly on purpose: the Java half has no ref-struct surface (UF050),
// while the .NET verify chain still exercises the lowering because it
// runs the preprocessed test project itself.
// =====================================================================

public ref struct RefStructPatternDisposeEnumerator
{
    private int _current;
    private readonly int _limit;
    private readonly DisposeProbe _probe;

    internal RefStructPatternDisposeEnumerator(int limit, DisposeProbe probe)
    {
        _limit = limit;
        _probe = probe;
        _current = 0;
    }

    public int Current => _current;

    public bool MoveNext() => ++_current <= _limit;

    public void Dispose() => _probe.Count++;
}

public ref struct RefStructPatternDisposeDuck
{
    private readonly int _limit;
    private readonly DisposeProbe _probe;

    public RefStructPatternDisposeDuck(int limit, DisposeProbe probe)
    {
        _limit = limit;
        _probe = probe;
    }

    public RefStructPatternDisposeEnumerator GetEnumerator()
        => new RefStructPatternDisposeEnumerator(_limit, _probe);
}

/// <summary>
/// Foreach entry points exercised by EnumeratorDuckTests' ref-struct cases.
/// </summary>
public static class RefStructDuckSamples
{
    /// <summary>M2: ref struct pattern Dispose runs once, never boxed.</summary>
    public static int RefStructPatternDisposeNormalCount(int limit)
    {
        var probe = new DisposeProbe();
        foreach (var item in new RefStructPatternDisposeDuck(limit, probe))
        {
            GC.KeepAlive(item);
        }

        return probe.Count;
    }

    /// <summary>M2: ref struct break disposes once and only the prefix is seen.</summary>
    public static (int DisposeCount, List<int> Seen) RefStructPatternDisposeBreakCount(
        int limit, int stopAt)
    {
        var probe = new DisposeProbe();
        var seen = new List<int>();
        foreach (var item in new RefStructPatternDisposeDuck(limit, probe))
        {
            seen.Add(item);
            if (item == stopAt)
            {
                break;
            }
        }

        return (probe.Count, seen);
    }

    /// <summary>M2: a body exception still triggers the ref struct pattern Dispose.</summary>
    public static int RefStructPatternDisposeThrowCount(int limit)
    {
        var probe = new DisposeProbe();
        try
        {
            foreach (var item in new RefStructPatternDisposeDuck(limit, probe))
            {
                if (item == 2)
                {
                    throw new InvalidOperationException("ref-struct boom");
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Swallow so the probe count can be observed by the caller.
        }

        return probe.Count;
    }
}
