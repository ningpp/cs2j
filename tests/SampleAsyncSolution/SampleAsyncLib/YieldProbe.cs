using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SampleAsyncLib;

/// <summary>
/// C38 M4 fixture: three async-iterator producers written with the native
/// <c>async IAsyncEnumerable&lt;T&gt;</c> + <c>yield</c> syntax. The
/// async-normalize pass lowers them into non-async factory methods plus
/// explicit <c>Cs2jAsyncIterator_*</c>/<c>Cs2jAsyncEnumerable_*</c> state
/// machines; T11–T14 pin identical observable behavior on both trees.
/// </summary>
public static class YieldProbe
{
    private static int _enterCount;
    private static int _finallyCount;
    private static readonly object Gate = new();

    /// <summary>How many times a finally-iterator body was entered.</summary>
    public static int EnterCount
    {
        get { lock (Gate) { return _enterCount; } }
    }

    /// <summary>How many times a finally-iterator finally block ran.</summary>
    public static int FinallyCount
    {
        get { lock (Gate) { return _finallyCount; } }
    }

    /// <summary>Clears the enter/finally counters (call before a probe run).</summary>
    public static void ResetCounters()
    {
        lock (Gate)
        {
            _enterCount = 0;
            _finallyCount = 0;
        }
    }

    /// <summary>
    /// T11: yields 0..count-1 in order with a real asynchronous delay between
    /// elements; the enumerator token is merged via
    /// <see cref="EnumeratorCancellationAttribute"/> and observed both
    /// explicitly and by Task.Delay.
    /// </summary>
    public static async IAsyncEnumerable<int> RangeDelayed(
        int count,
        int delayMs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(delayMs, cancellationToken);
            yield return i;
        }
    }

    /// <summary>
    /// T12: yields 0..stopAfter in order, then terminates early via
    /// <c>yield break</c> (elements after the break are never produced).
    /// </summary>
    public static async IAsyncEnumerable<int> UntilBreak(int stopAfter)
    {
        var i = 0;
        while (true)
        {
            await Task.Delay(1);
            yield return i;
            if (i >= stopAfter)
            {
                yield break;
            }

            i++;
        }
    }

    /// <summary>
    /// T13/T14: yields 0..count-1 inside a try/finally; entry is counted on
    /// entry and the static finally counter is incremented on every exit path
    /// (normal exhaustion, yield break, or early DisposeAsync abandonment).
    /// The inner await foreach over <see cref="RangeDelayed"/> is the joint
    /// C38 site: expanded inside the producer before M4 lowers the producer
    /// itself, and the merged token is forwarded.
    /// </summary>
    public static async IAsyncEnumerable<int> BoundedWithFinally(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            _enterCount++;
        }

        try
        {
            await foreach (var x in RangeDelayed(count, 1, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return x;
            }
        }
        finally
        {
            lock (Gate)
            {
                _finallyCount++;
            }
        }
    }
}
