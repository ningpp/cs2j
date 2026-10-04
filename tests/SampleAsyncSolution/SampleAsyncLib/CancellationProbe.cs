using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SampleAsyncLib;

/// <summary>
/// C38 fixture: a hand-written cancellable asynchronous source. The
/// enumerator saved by GetAsyncEnumerator is consulted on every
/// MoveNextAsync via <see cref="CancellationToken.ThrowIfCancellationRequested"/>,
/// and DisposeAsync is observable (flag + counter, never throws here).
/// </summary>
public sealed class CancellationProbe : IAsyncEnumerable<int>
{
    private readonly IReadOnlyList<int> _sequence;

    public CancellationProbe(IReadOnlyList<int> sequence)
    {
        _sequence = sequence;
    }

    /// <summary>How many times DisposeAsync ran on the current enumerator.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>True once DisposeAsync ran.</summary>
    public bool DisposeCalled => DisposeCount > 0;

    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(this, cancellationToken);

    /// <summary>
    /// Consumes the source with WithCancellation. <paramref name="onItem"/>
    /// runs synchronously after each element arrives, so a test can request
    /// cancellation after observing the first element (mid-enumeration case).
    /// </summary>
    public static async Task ConsumeAsync(
        CancellationProbe source,
        CancellationToken cancellationToken,
        Action<int>? onItem = null)
    {
        await foreach (var value in source.WithCancellation(cancellationToken))
        {
            onItem?.Invoke(value);
        }
    }

    private sealed class Enumerator : IAsyncEnumerator<int>
    {
        private readonly CancellationProbe _owner;
        private readonly CancellationToken _cancellationToken;
        private int _index = -1;

        public Enumerator(CancellationProbe owner, CancellationToken cancellationToken)
        {
            _owner = owner;
            _cancellationToken = cancellationToken;
        }

        public int Current { get; private set; }

        public ValueTask<bool> MoveNextAsync()
        {
            _cancellationToken.ThrowIfCancellationRequested();

            _index++;
            if (_index < _owner._sequence.Count)
            {
                Current = _owner._sequence[_index];
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }

        public ValueTask DisposeAsync()
        {
            _owner.DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
