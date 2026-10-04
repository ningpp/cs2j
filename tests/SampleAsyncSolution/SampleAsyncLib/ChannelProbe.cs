using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SampleAsyncLib;

/// <summary>
/// C38 fixture: the real asynchronous producer side, built on
/// <see cref="System.Threading.Channels.Channel{T}"/>. A background
/// <see cref="Task"/> writes 1..N (optionally with a per-element delay) into the
/// channel; consumers read it through the BCL async iterator exposed by
/// <see cref="ChannelReader{T}.ReadAllAsync(CancellationToken)"/>.
///
/// The async-iterator state machine lives in the BCL (ReadAllAsync is a
/// framework method), NOT in a repo-owned producer: this fixture deliberately
/// contains no 'async IAsyncEnumerable + yield' method of its own (CS2J1844 /
/// M4 territory). The bounded capacity of 1 gives non-trivial
/// producer/consumer scheduling.
/// </summary>
public sealed class ChannelProbe
{
    private const int BoundedCapacity = 1;

    private readonly Channel<int> _channel;
    private readonly int _count;
    private readonly int _delayMs;
    private readonly CancellationToken _cancellationToken;

    private ChannelProbe(
        Channel<int> channel, int count, int delayMs, CancellationToken cancellationToken)
    {
        _channel = channel;
        _count = count;
        _delayMs = delayMs;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Reader exposed to async-stream consumers.</summary>
    public ChannelReader<int> Reader => _channel.Reader;

    /// <summary>The background producer task (completes when the channel is completed).</summary>
    public Task ProducerTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Starts a bounded producer (capacity 1) writing 1..<paramref name="count"/>
    /// with a <paramref name="delayMs"/> delay after every write.
    /// </summary>
    public static ChannelProbe StartBounded(
        int count, int delayMs, CancellationToken cancellationToken = default)
    {
        var probe = new ChannelProbe(
            Channel.CreateBounded<int>(BoundedCapacity), count, delayMs, cancellationToken);
        probe.ProducerTask = Task.Run(probe.ProduceAsync);
        return probe;
    }

    /// <summary>Starts an unbounded producer writing 1..<paramref name="count"/>.</summary>
    public static ChannelProbe StartUnbounded(
        int count, CancellationToken cancellationToken = default)
    {
        var probe = new ChannelProbe(
            Channel.CreateUnbounded<int>(), count, delayMs: 0, cancellationToken);
        probe.ProducerTask = Task.Run(probe.ProduceAsync);
        return probe;
    }

    private async Task ProduceAsync()
    {
        for (var value = 1; value <= _count; value++)
        {
            await _channel.Writer.WriteAsync(value, _cancellationToken);
            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, _cancellationToken);
            }
        }

        _channel.Writer.Complete();
    }

    /// <summary>
    /// Wraps the channel reader as an <see cref="IAsyncEnumerable{T}"/> via the
    /// BCL; contains no yield syntax (ReadAllAsync is the framework iterator).
    /// </summary>
    public static IAsyncEnumerable<int> WrapReader(
        ChannelReader<int> reader, CancellationToken cancellationToken = default)
        => reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Consumer over the BCL channel async iterator (ReadAllAsync). The
    /// framework iterator is materialized into an interface-typed local first
    /// (ReadAllAsync already carries the token; the C38 v1 chain resolver
    /// only strips WithCancellation/ConfigureAwait off the foreach source),
    /// then consumed by a bare await-foreach — the C38 consumption site.
    /// </summary>
    public static async Task<List<int>> CollectReaderAsync(
        ChannelReader<int> reader, CancellationToken cancellationToken = default)
    {
        var items = new List<int>();
        IAsyncEnumerable<int> entries = reader.ReadAllAsync(cancellationToken);
        await foreach (var value in entries)
        {
            items.Add(value);
        }

        return items;
    }

    /// <summary>
    /// Consumer over a plain <see cref="IAsyncEnumerable{T}"/> (bare
    /// await-foreach, no WithCancellation — default CancellationToken.None).
    /// </summary>
    public static async Task<List<int>> CollectAsync(IAsyncEnumerable<int> source)
    {
        var items = new List<int>();
        await foreach (var value in source)
        {
            items.Add(value);
        }

        return items;
    }
}
