using System;
using System.Collections;
using System.Collections.Generic;

namespace SampleForeachSourceLib;

// =====================================================================
// C40 foreach-source M1 fixture: duck-typed enumerator families.
// None of the collections below implement IEnumerable/IEnumerable<T>;
// foreach binds them through the pattern (instance/extension GetEnumerator).
// =====================================================================

// FA-1: plain instance duck. Enumerator has no IDisposable and no Dispose().
public sealed class PlainDuckEnumerator
{
    private int _current;

    public int Current => _current;

    public bool MoveNext()
    {
        if (_current >= 3)
        {
            return false;
        }

        _current++;
        return true;
    }
}

public sealed class PlainDuck
{
    public PlainDuckEnumerator GetEnumerator() => new PlainDuckEnumerator();
}

public sealed class PlainDuckFactory
{
    public int SourceCallCount;

    public PlainDuck Create()
    {
        SourceCallCount++;
        return new PlainDuck();
    }
}

// M1 probe-pinned: a public Dispose() WITHOUT IDisposable is never invoked
// by foreach on Roslyn 5.0 (verified via emitted-assembly probes). The
// lowering must keep this site a bare while loop and leave the flag at 0.
public sealed class PatternDisposeEnumerator
{
    public int DisposeCount;
    private int _current;

    public int Current => _current;

    public bool MoveNext() => ++_current <= 2;

    public void Dispose() => DisposeCount++;
}

public sealed class PatternDisposeDuck
{
    public PatternDisposeEnumerator Enumerator { get; } = new PatternDisposeEnumerator();

    public PatternDisposeEnumerator GetEnumerator() => Enumerator;
}

// FA-4: enumerator implementing IDisposable, with an observable dispose flag
// for normal-completion / break / body-exception / continue timing probes.
public sealed class FlaggedEnumerator : IDisposable
{
    public int DisposeCount;
    private readonly int _limit;
    private int _current;

    public FlaggedEnumerator(int limit = 3)
    {
        _limit = limit;
    }

    public int Current => _current;

    public bool MoveNext() => ++_current <= _limit;

    public void Dispose() => DisposeCount++;
}

public sealed class FlaggedDuck
{
    public FlaggedEnumerator Enumerator { get; }

    public FlaggedDuck(int limit = 3)
    {
        Enumerator = new FlaggedEnumerator(limit);
    }

    public FlaggedEnumerator GetEnumerator() => Enumerator;
}

// FA-2: extension GetEnumerator on a non-IEnumerable type.
public sealed class ExtensionEnumerable
{
    public int Limit { get; }

    public ExtensionEnumerable(int limit = 3)
    {
        Limit = limit;
    }
}

public sealed class ExtensionEnumerator
{
    private int _current;
    private readonly int _limit;

    public ExtensionEnumerator(int limit)
    {
        _limit = limit;
    }

    public int Current => _current;

    public bool MoveNext() => ++_current <= _limit;
}

public static class EnumeratorDuckExtensions
{
    public static ExtensionEnumerator GetEnumerator(this ExtensionEnumerable source)
        => new ExtensionEnumerator(source.Limit);
}

// Legal shape nearest to "direct enumerator variables": a pattern
// GetEnumerator whose static return type is IEnumerator<T>/IEnumerator.
// (Bare `foreach (var x in someIEnumerator)` is CS1579 and cannot compile.)
// IEnumerator<T> implements IDisposable; on net10 the non-generic
// IEnumerator no longer lists IDisposable.
//
// The enumerators come from BCL collections (List<T>.Enumerator /
// ArrayList.Enumerator are metadata types whose explicit non-generic
// Current implementation never enters source-level desugaring), so the
// Java face only needs to model the boxed IEnumerator foreach contract.
public sealed class GenericBoxedEnumerable
{
    private readonly int[] _values;

    public GenericBoxedEnumerable(params int[] values)
    {
        _values = values;
    }

    public IEnumerator<int> GetEnumerator()
        => new List<int>(_values).GetEnumerator();
}

public sealed class NonGenericBoxedEnumerable
{
    private readonly object[] _values;

    public NonGenericBoxedEnumerable(params object[] values)
    {
        _values = values;
    }

    public IEnumerator GetEnumerator()
        => new System.Collections.ArrayList(_values).GetEnumerator();
}

// =====================================================================
// C40 M2 fixtures.
// =====================================================================

// Shared reference-type sink: struct enumerators copied onto the heap by an
// IDisposable interface cast still mutate the same probe target, so the
// exactly-once Dispose observation survives value-type copying.
public sealed class DisposeProbe
{
    public int Count;
}

// M2 FA-4 matrix: stateful value-type enumerator without IDisposable. The
// enumeration state lives in the struct local; a lowering that went through
// an interface box would drive a frozen copy (dead loop / constant values),
// so the bare unboxed while skeleton is the only correct shape.
public struct StatefulStructEnumerator
{
    private int _current;
    private readonly int _limit;

    internal StatefulStructEnumerator(int limit)
    {
        _limit = limit;
        _current = 0;
    }

    public int Current => _current;

    public bool MoveNext() => ++_current <= _limit;
}

public sealed class StatefulStructDuck
{
    private readonly int _limit;

    public StatefulStructDuck(int limit = 3)
    {
        _limit = limit;
    }

    public StatefulStructEnumerator GetEnumerator()
        => new StatefulStructEnumerator(_limit);
}

// M2 CS2J5455: value-type enumerator whose only Dispose is an explicit
// IDisposable implementation. The pattern Dispose is privately inaccessible,
// so the lowered site boxes through ((global::System.IDisposable)e).Dispose();
// the probe is a shared reference so the boxed copy is still observable.
public struct ExplicitDisposeStructEnumerator : IDisposable
{
    private int _current;
    private readonly int _limit;
    private readonly DisposeProbe _probe;

    internal ExplicitDisposeStructEnumerator(int limit, DisposeProbe probe)
    {
        _limit = limit;
        _probe = probe;
        _current = 0;
    }

    public int Current => _current;

    public bool MoveNext() => ++_current <= _limit;

    void IDisposable.Dispose() => _probe.Count++;
}

public sealed class ExplicitDisposeStructDuck
{
    private readonly int _limit;

    public ExplicitDisposeStructDuck(int limit = 3)
    {
        _limit = limit;
        Probe = new DisposeProbe();
    }

    public DisposeProbe Probe { get; }

    public ExplicitDisposeStructEnumerator GetEnumerator()
        => new ExplicitDisposeStructEnumerator(_limit, Probe);
}

// M2 ref-struct fixtures and their sample entry points live in
// RefStructEnumeratorDuck.cs (kept apart so the Java half can compile/run
// the non-ref duck families independently).

/// <summary>
/// Foreach entry points exercised by EnumeratorDuckTests. The chain runs the
/// test project on the lowered intermediate tree, so every assertion here
/// validates the generated GetEnumerator/while/Current/try-finally skeleton.
/// </summary>
public static class EnumeratorDuckSamples
{
    public static List<int> CollectPlain(PlainDuck duck)
    {
        var result = new List<int>();
        foreach (var item in duck)
        {
            result.Add(item);
        }

        return result;
    }

    public static List<int> CollectFromFactory(PlainDuckFactory factory)
    {
        var result = new List<int>();
        foreach (var item in factory.Create())
        {
            result.Add(item);
        }

        return result;
    }

    public static List<int> CollectExtension(ExtensionEnumerable source)
    {
        var result = new List<int>();
        foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    public static List<int> CollectGenericBoxed(GenericBoxedEnumerable source)
    {
        var result = new List<int>();
        foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    public static List<int> CollectNonGenericBoxed(NonGenericBoxedEnumerable source)
    {
        var result = new List<int>();
        foreach (int item in source)
        {
            result.Add(item);
        }

        return result;
    }

    /// <summary>Normal completion: Dispose runs exactly once at loop exit.</summary>
    public static int NormalCompletionDisposeCount(FlaggedDuck duck)
    {
        foreach (var item in duck)
        {
            _ = item;
        }

        return duck.Enumerator.DisposeCount;
    }

    /// <summary>break: Dispose runs exactly once, only the first item is seen.</summary>
    public static int BreakAfterFirst(FlaggedDuck duck, List<int> seen)
    {
        foreach (var item in duck)
        {
            seen.Add(item);
            break;
        }

        return duck.Enumerator.DisposeCount;
    }

    /// <summary>Exception in the body bubbles out after Dispose executes.</summary>
    public static void ThrowAtSecond(FlaggedDuck duck)
    {
        foreach (var item in duck)
        {
            if (item == 2)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }

    /// <summary>continue never disposes mid-loop; Dispose runs once at the end.</summary>
    public static int ContinueObservations(FlaggedDuck duck, List<int> midDisposeCounts)
    {
        foreach (var item in duck)
        {
            midDisposeCounts.Add(duck.Enumerator.DisposeCount);
            continue;
        }

        return duck.Enumerator.DisposeCount;
    }

    // NOTE: CaptureActions (per-iteration lambda capture, Decision 12) lives in
    // the Tests project: its Func/delegate surface is the closure-lift/delegate
    // conversion contract, not the foreach-skeleton C40 Java face.

    // ----- M2 entry points -----

    /// <summary>M2: stateful struct enumerator stays unboxed; all values arrive.</summary>
    public static List<int> CollectStatefulStruct(StatefulStructDuck duck)
    {
        var result = new List<int>();
        foreach (var item in duck)
        {
            result.Add(item);
        }

        return result;
    }

    /// <summary>M2: break on a stateful struct enumerator yields the prefix only.</summary>
    public static List<int> BreakStatefulStruct(StatefulStructDuck duck, int stopAt)
    {
        var result = new List<int>();
        foreach (var item in duck)
        {
            if (item == stopAt)
            {
                break;
            }

            result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// M2 CS2J5455: explicit-interface struct Dispose runs exactly once on
    /// normal completion (observable through the shared probe).
    /// </summary>
    public static List<int> CollectExplicitDisposeStruct(ExplicitDisposeStructDuck duck)
    {
        var result = new List<int>();
        foreach (var item in duck)
        {
            result.Add(item);
        }

        return result;
    }

    /// <summary>M2 CS2J5455: break path also disposes the boxed struct once.</summary>
    public static List<int> BreakExplicitDisposeStruct(ExplicitDisposeStructDuck duck)
    {
        var result = new List<int>();
        foreach (var item in duck)
        {
            result.Add(item);
            if (item == 2)
            {
                break;
            }
        }

        return result;
    }
}
