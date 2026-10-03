using System;
using System.Collections.Generic;
using System.Threading;

namespace SampleConcurrencyLib;

/// <summary>
/// C29 CN-3 fixture (design §7.2): the four TryEnter shapes — no timeout
/// (normalized with an explicit 0ms timeout), int milliseconds, TimeSpan
/// (lifted to an int-milliseconds temp) and the lockTaken ref-bool form
/// (canonical three-arg shape, structure-checked only). The fallback branch
/// of every timed shape is observable through the attempt log.
/// </summary>
public sealed class TimedGate
{
    private readonly object _gate = new();
    private readonly object _logGate = new();
    private readonly List<string> _attempts = new();

    public IReadOnlyList<string> Attempts
    {
        get
        {
            lock (_logGate)
            {
                return _attempts.ToArray();
            }
        }
    }

    private void Record(string entry)
    {
        lock (_logGate)
        {
            _attempts.Add(entry);
        }
    }

    /// <summary>Shape 1: TryEnter(object) — CN-3 appends the explicit 0ms timeout.</summary>
    public bool TryEnterWithoutTimeout(Action? holder)
    {
        if (Monitor.TryEnter(_gate))
        {
            try
            {
                holder?.Invoke();
                Record("no-timeout:acquired");
                return true;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
        else
        {
            Record("no-timeout:fallback");
            return false;
        }
    }

    /// <summary>Shape 2: TryEnter(object, int milliseconds).</summary>
    public bool TryEnterWithinMilliseconds(int timeoutMilliseconds, Action? holder)
    {
        if (Monitor.TryEnter(_gate, timeoutMilliseconds))
        {
            try
            {
                holder?.Invoke();
                Record("int-ms:acquired");
                return true;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
        else
        {
            Record("int-ms:fallback");
            return false;
        }
    }

    /// <summary>Shape 3: TryEnter(object, TimeSpan).</summary>
    public bool TryEnterWithinTimeSpan(TimeSpan timeout, Action? holder)
    {
        if (Monitor.TryEnter(_gate, timeout))
        {
            try
            {
                holder?.Invoke();
                Record("timespan:acquired");
                return true;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }
        else
        {
            Record("timespan:fallback");
            return false;
        }
    }

    /// <summary>
    /// Shape 4: the lockTaken ref-bool form. The body must not read the taken
    /// local (CN-3 exclusivity), so the held section is observed through the
    /// holder action and the attempt log instead of the taken variable.
    /// </summary>
    public void TryEnterLockTakenForm(Action? holder)
    {
        bool taken = false;
        Monitor.TryEnter(_gate, 30, ref taken);
        try
        {
            holder?.Invoke();
            Record("lock-taken:attempted");
        }
        finally
        {
            if (taken) Monitor.Exit(_gate);
        }
    }
}
