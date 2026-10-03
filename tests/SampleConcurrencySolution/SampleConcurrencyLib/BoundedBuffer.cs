using System;
using System.Collections.Generic;
using System.Threading;

namespace SampleConcurrencyLib;

/// <summary>
/// C29 fixture producer/consumer monitor (design §7.2): Wait/Pulse/PulseAll
/// around a bounded queue, including the timed Wait(int) bool-return branch
/// and a Wait(TimeSpan) overload that CN-4 lifts to an int-milliseconds temp.
/// Every Wait/Pulse/PulseAll sits lexically inside the lock on the same field
/// symbol, so the CN ownership check proves the call sites.
/// </summary>
public sealed class BoundedBuffer<T>
{
    private readonly object _sync = new();
    private readonly Queue<T> _items = new();
    private readonly int _capacity;
    private bool _closed;

    public BoundedBuffer(int capacity)
    {
        _capacity = capacity > 0 ? capacity : 1;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    public bool IsClosed
    {
        get
        {
            lock (_sync)
            {
                return _closed;
            }
        }
    }

    /// <summary>Enqueues; blocks in an un-timed Wait while the buffer is full.</summary>
    public void Put(T item)
    {
        lock (_sync)
        {
            while (_items.Count == _capacity)
            {
                Monitor.Wait(_sync);
            }

            _items.Enqueue(item);
            Monitor.Pulse(_sync);
        }
    }

    /// <summary>
    /// Dequeues; blocks in an un-timed Wait while empty and throws once the
    /// buffer was closed and drained (the PulseAll wakeup path).
    /// </summary>
    public T Take()
    {
        lock (_sync)
        {
            while (_items.Count == 0)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("the buffer was closed and drained");
                }

                Monitor.Wait(_sync);
            }

            var item = _items.Dequeue();
            Monitor.Pulse(_sync);
            return item;
        }
    }

    /// <summary>Timed Wait(int) with the bool return branch preserved by CN-4.</summary>
    public bool TryTake(int timeoutMilliseconds, out T item)
    {
        lock (_sync)
        {
            while (_items.Count == 0)
            {
                if (_closed)
                {
                    item = default!;
                    return false;
                }

                if (!Monitor.Wait(_sync, timeoutMilliseconds))
                {
                    item = default!;
                    return false;
                }
            }

            item = _items.Dequeue();
            Monitor.Pulse(_sync);
            return true;
        }
    }

    /// <summary>Wait(TimeSpan) — CN-4 lifts the timeout to an int-milliseconds temp.</summary>
    public bool WaitUntilDrained(TimeSpan timeout)
    {
        lock (_sync)
        {
            while (_items.Count > 0)
            {
                if (!Monitor.Wait(_sync, timeout))
                {
                    return _items.Count == 0;
                }
            }

            return true;
        }
    }

    /// <summary>Closes the buffer and wakes every waiter via PulseAll.</summary>
    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
            Monitor.PulseAll(_sync);
        }
    }
}
