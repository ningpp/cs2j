using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SampleAsyncLib;

/// <summary>
/// C38 fixture: an <see cref="IAsyncDisposable"/> resource that records the
/// release order into a static list (for nested LIFO assertions) and can be
/// configured to throw a chosen exception from DisposeAsync.
/// </summary>
public sealed class AsyncDisposalProbe : IAsyncDisposable
{
    private static readonly List<string> DisposalOrder = new();
    private static readonly object Gate = new();

    private readonly Exception? _disposeError;

    private AsyncDisposalProbe(string name, Exception? disposeError)
    {
        Name = name;
        _disposeError = disposeError;
    }

    /// <summary>Resource name recorded on disposal.</summary>
    public string Name { get; }

    /// <summary>How many times DisposeAsync ran on this instance.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>True once DisposeAsync ran.</summary>
    public bool DisposeCalled => DisposeCount > 0;

    /// <summary>Factory used by every await-using case.</summary>
    public static AsyncDisposalProbe Create(string name, Exception? disposeError = null)
        => new(name, disposeError);

    /// <summary>Clears the shared release-order log (call before a scope run).</summary>
    public static void ResetOrder()
    {
        lock (Gate)
        {
            DisposalOrder.Clear();
        }
    }

    /// <summary>Snapshot of the shared release-order log.</summary>
    public static IReadOnlyList<string> SnapshotOrder()
    {
        lock (Gate)
        {
            return DisposalOrder.ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (Gate)
        {
            DisposalOrder.Add(Name);
        }

        DisposeCount++;
        if (_disposeError != null)
        {
            return ValueTask.FromException(_disposeError);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// T6: an await-using scope whose resource is a null reference must
    /// complete normally (compiler-emitted null guard), without throwing.
    /// </summary>
    public static async Task RunWithNullAsync()
    {
        await using (IAsyncDisposable? resource = null)
        {
            await Task.CompletedTask;
        }
    }

    /// <summary>T9: the normal completion path releases exactly once.</summary>
    public static async Task RunNormalBodyAsync(AsyncDisposalProbe resource)
    {
        await using (resource)
        {
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// T9: a pre-canceled cancellation token is observed and handled, then an
    /// await-using scope still runs and releases exactly once.
    /// </summary>
    public static async Task RunAfterCancellationObservedAsync(
        CancellationToken cancellationToken, AsyncDisposalProbe resource)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // The canceled context is observed; the scope below still runs.
        }

        await using (resource)
        {
            await Task.CompletedTask;
        }
    }
}
