using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SampleAsyncLib;

/// <summary>
/// C38 fixture: a fully hand-written dual implementation of
/// <see cref="IAsyncEnumerable{T}"/> + <see cref="IAsyncEnumerator{T}"/>
/// (class, no <c>yield</c>, no async state machine). Every step completes
/// synchronously through <see cref="ValueTask"/> helpers.
///
/// Injection knobs for the §4.6 exception-timing oracle:
/// <list type="bullet">
/// <item><term>throwOnFirstMoveNext</term><description>
/// the first MoveNextAsync fails with InvalidOperationException("body-in-flight").
/// </description></item>
/// <item><term>throwOnDispose</term><description>
/// DisposeAsync fails with ObjectDisposedException("dispose").
/// </description></item>
/// </list>
/// The token handed to GetAsyncEnumerator is recorded on the source instance
/// so WithCancellation visibility can be asserted.
/// </summary>
public sealed class HandwrittenAsyncEnumerable : IAsyncEnumerable<int>
{
    private readonly IReadOnlyList<int> _sequence;
    private readonly bool _throwOnFirstMoveNext;
    private readonly bool _throwOnDispose;

    public HandwrittenAsyncEnumerable(
        IReadOnlyList<int> sequence,
        bool throwOnFirstMoveNext = false,
        bool throwOnDispose = false)
    {
        _sequence = sequence;
        _throwOnFirstMoveNext = throwOnFirstMoveNext;
        _throwOnDispose = throwOnDispose;
    }

    /// <summary>Whether GetAsyncEnumerator was ever called.</summary>
    public bool TokenRecorded { get; private set; }

    /// <summary>The last token observed by GetAsyncEnumerator.</summary>
    public CancellationToken RecordedToken { get; private set; }

    /// <summary>How many enumerators were disposed (DisposeAsync invocations).</summary>
    public int DisposeCount { get; private set; }

    /// <summary>True once any enumerator's DisposeAsync ran.</summary>
    public bool DisposeCalled => DisposeCount > 0;

    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        TokenRecorded = true;
        RecordedToken = cancellationToken;
        return new Enumerator(this);
    }

    /// <summary>
    /// Consumption with WithCancellation (T2): the exact token must reach
    /// GetAsyncEnumerator.
    /// </summary>
    public static async Task ConsumeWithCancellationAsync(
        HandwrittenAsyncEnumerable source, CancellationToken cancellationToken)
    {
        await foreach (var value in source.WithCancellation(cancellationToken))
        {
            _ = value;
        }
    }

    /// <summary>
    /// Bare consumption (T2): the default-argument path hands
    /// <see cref="CancellationToken.None"/> to GetAsyncEnumerator.
    /// </summary>
    public static async Task ConsumeBareAsync(HandwrittenAsyncEnumerable source)
    {
        await foreach (var value in source)
        {
            _ = value;
        }
    }

    private sealed class Enumerator : IAsyncEnumerator<int>
    {
        private readonly HandwrittenAsyncEnumerable _owner;
        private int _index = -1;
        private bool _moveNextCalled;

        public Enumerator(HandwrittenAsyncEnumerable owner)
        {
            _owner = owner;
        }

        public int Current { get; private set; }

        public ValueTask<bool> MoveNextAsync()
        {
            if (!_moveNextCalled)
            {
                _moveNextCalled = true;
                if (_owner._throwOnFirstMoveNext)
                {
                    return ValueTask.FromException<bool>(
                        new InvalidOperationException("body-in-flight"));
                }
            }

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
            if (_owner._throwOnDispose)
            {
                return ValueTask.FromException(new ObjectDisposedException("dispose"));
            }

            return ValueTask.CompletedTask;
        }
    }
}
