using System;
using System.Threading;

namespace SampleConcurrencyLib;

/// <summary>
/// C29 CN-2 rewrite targets (design §7.2): hand-written Enter/Exit pairs in
/// the two compiler shapes — form A (Enter with a lockTaken ref) and form B
/// (old-style bare Enter) — plus in-lock throw methods whose release semantics
/// the multithreaded tests verify identically on the original source and on
/// the normalized intermediate tree.
/// </summary>
public sealed class HandwrittenMonitorLock
{
    private readonly object _gate = new();
    private int _formACount;
    private int _formBCount;
    private int _probeCount;

    public int FormACount => _formACount;

    public int FormBCount => _formBCount;

    public int ProbeCount => _probeCount;

    /// <summary>CN-2 form A: the compiler-style Enter with a lockTaken ref.</summary>
    public void IncrementFormA()
    {
        bool taken = false;
        Monitor.Enter(_gate, ref taken);
        try
        {
            _formACount++;
        }
        finally
        {
            if (taken) Monitor.Exit(_gate);
        }
    }

    /// <summary>CN-2 form B: the old-style bare Enter plus try/finally Exit.</summary>
    public void IncrementFormB()
    {
        Monitor.Enter(_gate);
        try
        {
            _formBCount++;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Throw released by the lock statement lowering (CN-1 semantics).</summary>
    public void ThrowInsideDirectLockWhen(Action? beforeThrow)
    {
        lock (_gate)
        {
            beforeThrow?.Invoke();
            throw new InvalidOperationException("direct-lock-throw");
        }
    }

    /// <summary>Throw inside a form-A handwritten region.</summary>
    public void ThrowInsideFormAWhen(Action? beforeThrow)
    {
        bool taken = false;
        Monitor.Enter(_gate, ref taken);
        try
        {
            beforeThrow?.Invoke();
            throw new InvalidOperationException("form-a-throw");
        }
        finally
        {
            if (taken) Monitor.Exit(_gate);
        }
    }

    /// <summary>Throw inside a form-B handwritten region.</summary>
    public void ThrowInsideFormBWhen(Action? beforeThrow)
    {
        Monitor.Enter(_gate);
        try
        {
            beforeThrow?.Invoke();
            throw new InvalidOperationException("form-b-throw");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Blocking re-entry probe used by the tests (lock direct mapping).</summary>
    public void ProbeAcquire()
    {
        lock (_gate)
        {
            _probeCount++;
        }
    }
}
