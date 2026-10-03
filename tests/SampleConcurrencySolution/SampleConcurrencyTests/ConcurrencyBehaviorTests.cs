using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SampleConcurrencyLib;
using Xunit;

namespace SampleConcurrencyTests;

/// <summary>
/// .NET-side real-concurrency contract for the C29 fixture APIs (design §7.2).
/// These tests run with identical semantics on the original source (baseline)
/// and on the concurrency-normalize intermediate tree — the normalization must
/// not change observable behavior. Every test carries a watchdog so a lost
/// wakeup or a broken release can fail the run instead of hanging CI.
/// </summary>
public class ConcurrencyBehaviorTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    // ------------------------------------------------------------------
    // Producer / consumer over BoundedBuffer.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProducerConsumer_AllElementsConsumedExactlyOnce()
    {
        const int producerCount = 4;
        const int consumerCount = 3;
        const int itemsPerProducer = 250;
        var total = producerCount * itemsPerProducer;
        var buffer = new BoundedBuffer<int>(capacity: 8);
        var received = new int[total];
        var producersDone = 0;
        using var watchdog = new CancellationTokenSource(Watchdog);

        var producers = Enumerable.Range(0, producerCount).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < itemsPerProducer; i++)
            {
                watchdog.Token.ThrowIfCancellationRequested();
                buffer.Put(p * itemsPerProducer + i);
            }

            Interlocked.Increment(ref producersDone);
        }, watchdog.Token)).ToArray();

        var consumers = Enumerable.Range(0, consumerCount).Select(_ => Task.Run(() =>
        {
            while (true)
            {
                watchdog.Token.ThrowIfCancellationRequested();
                if (buffer.TryTake(50, out var item))
                {
                    Interlocked.Increment(ref received[item]);
                    continue;
                }

                if (producersDone == producerCount && buffer.Count == 0)
                {
                    return;
                }
            }
        }, watchdog.Token)).ToArray();

        await Task.WhenAll(producers).WaitAsync(watchdog.Token);
        buffer.Close();
        await Task.WhenAll(consumers).WaitAsync(watchdog.Token);

        Assert.All(received, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task ProducerConsumer_SingleConsumer_PreservesPerProducerFifoOrder()
    {
        const int producerCount = 3;
        const int itemsPerProducer = 200;
        var buffer = new BoundedBuffer<int>(capacity: 4);
        var lastSeen = new int[producerCount];
        var orderViolations = 0;
        var consumedCount = 0;
        var producersDone = 0;
        using var watchdog = new CancellationTokenSource(Watchdog);

        var producers = Enumerable.Range(0, producerCount).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < itemsPerProducer; i++)
            {
                watchdog.Token.ThrowIfCancellationRequested();
                // 1-based ids so each producer's stream is 1..itemsPerProducer.
                buffer.Put(p * itemsPerProducer + i + 1);
            }

            Interlocked.Increment(ref producersDone);
        }, watchdog.Token)).ToArray();

        var consumer = Task.Run(() =>
        {
            while (true)
            {
                watchdog.Token.ThrowIfCancellationRequested();
                if (buffer.TryTake(50, out var item))
                {
                    var producer = (item - 1) / itemsPerProducer;
                    var sequence = ((item - 1) % itemsPerProducer) + 1;
                    if (sequence != lastSeen[producer] + 1)
                    {
                        orderViolations++;
                    }

                    lastSeen[producer] = sequence;
                    consumedCount++;
                    continue;
                }

                if (producersDone == producerCount && buffer.Count == 0)
                {
                    return;
                }
            }
        }, watchdog.Token);

        await Task.WhenAll(producers).WaitAsync(watchdog.Token);
        buffer.Close();
        await consumer.WaitAsync(watchdog.Token);

        Assert.Equal(producerCount * itemsPerProducer, consumedCount);
        Assert.Equal(0, orderViolations);
        Assert.All(lastSeen, seen => Assert.Equal(itemsPerProducer, seen));
    }

    [Fact]
    public async Task ProducerConsumer_PulseWakesBlockedTakers()
    {
        var buffer = new BoundedBuffer<string>(capacity: 2);
        using var watchdog = new CancellationTokenSource(Watchdog);

        var takerA = Task.Run(() => buffer.Take(), watchdog.Token);
        var takerB = Task.Run(() => buffer.Take(), watchdog.Token);
        await Task.Delay(200, watchdog.Token);

        buffer.Put("first");
        buffer.Put("second");

        var items = await Task.WhenAll(takerA, takerB).WaitAsync(watchdog.Token);
        Assert.Equal(2, items.Distinct().Count());
        Assert.All(items, item => Assert.True(item is "first" or "second"));
    }

    [Fact]
    public async Task ProducerConsumer_PulseAllReleasesWaitersOnClose()
    {
        var buffer = new BoundedBuffer<int>(capacity: 2);
        using var watchdog = new CancellationTokenSource(Watchdog);

        var takerA = Task.Run(() => buffer.Take(), watchdog.Token);
        var takerB = Task.Run(() => buffer.Take(), watchdog.Token);
        await Task.Delay(200, watchdog.Token);

        buffer.Close();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.WhenAll(takerA, takerB).WaitAsync(watchdog.Token));
        Assert.True(buffer.IsClosed);
    }

    [Fact]
    public async Task ProducerConsumer_WaitUntilDrained_BothBoolBranches()
    {
        var buffer = new BoundedBuffer<int>(capacity: 4);
        using var watchdog = new CancellationTokenSource(Watchdog);

        // Timeout branch: an undrained buffer makes the timed Wait return false.
        buffer.Put(1);
        Assert.False(await Task.Run(
            () => buffer.WaitUntilDrained(TimeSpan.FromMilliseconds(100)), watchdog.Token));

        buffer.Take();
        Assert.True(await Task.Run(
            () => buffer.WaitUntilDrained(TimeSpan.FromSeconds(5)), watchdog.Token));
    }

    // ------------------------------------------------------------------
    // TimedGate: the timed TryEnter timeout branches are actually reachable.
    // ------------------------------------------------------------------

    [Fact]
    public async Task TimedGate_Uncontended_EveryShapeAcquires()
    {
        var gate = new TimedGate();
        using var watchdog = new CancellationTokenSource(Watchdog);

        Assert.True(await Task.Run(() => gate.TryEnterWithoutTimeout(null), watchdog.Token));
        Assert.True(await Task.Run(() => gate.TryEnterWithinMilliseconds(25, null), watchdog.Token));
        Assert.True(await Task.Run(
            () => gate.TryEnterWithinTimeSpan(TimeSpan.FromMilliseconds(25), null), watchdog.Token));
        await Task.Run(() => gate.TryEnterLockTakenForm(null), watchdog.Token);

        var attempts = gate.Attempts;
        Assert.Contains("no-timeout:acquired", attempts);
        Assert.Contains("int-ms:acquired", attempts);
        Assert.Contains("timespan:acquired", attempts);
        Assert.Contains("lock-taken:attempted", attempts);
        Assert.DoesNotContain("no-timeout:fallback", attempts);
        Assert.DoesNotContain("int-ms:fallback", attempts);
        Assert.DoesNotContain("timespan:fallback", attempts);
    }

    [Fact]
    public async Task TimedGate_TimeoutFallbackReached_WhileAnotherThreadHolds()
    {
        var gate = new TimedGate();
        using var watchdog = new CancellationTokenSource(Watchdog);
        using var holderReady = new ManualResetEventSlim(false);
        using var releaseHolder = new ManualResetEventSlim(false);

        var holder = Task.Run(() => gate.TryEnterWithinMilliseconds(
            10_000,
            () =>
            {
                holderReady.Set();
                releaseHolder.Wait(watchdog.Token);
            }), watchdog.Token);

        Assert.True(holderReady.Wait(TimeSpan.FromSeconds(10)), "holder never acquired the gate");

        // While the holder owns the gate, every timed shape must exhaust its
        // timeout and take the fallback branch — the branch CS2J2911 keeps
        // explicit on the normalized tree.
        Assert.False(await Task.Run(() => gate.TryEnterWithoutTimeout(null), watchdog.Token));
        Assert.False(await Task.Run(() => gate.TryEnterWithinMilliseconds(50, null), watchdog.Token));
        Assert.False(await Task.Run(
            () => gate.TryEnterWithinTimeSpan(TimeSpan.FromMilliseconds(50), null), watchdog.Token));
        await Task.Run(() => gate.TryEnterLockTakenForm(null), watchdog.Token);

        var attempts = gate.Attempts;
        Assert.Contains("no-timeout:fallback", attempts);
        Assert.Contains("int-ms:fallback", attempts);
        Assert.Contains("timespan:fallback", attempts);

        releaseHolder.Set();
        Assert.True(await holder.WaitAsync(watchdog.Token), "holder could not release the gate");
        // The holder records its acquisition only after its held section ends,
        // so re-read the log now that it completed.
        Assert.Contains("int-ms:acquired", gate.Attempts);
    }

    // ------------------------------------------------------------------
    // In-lock throw must release the lock for other threads.
    // ------------------------------------------------------------------

    public enum ThrowShape
    {
        DirectLock,
        FormA,
        FormB,
    }

    [Theory]
    [InlineData(ThrowShape.DirectLock)]
    [InlineData(ThrowShape.FormA)]
    [InlineData(ThrowShape.FormB)]
    public async Task ThrowInsideLock_OtherThreadReentersWithinWatchdog(ThrowShape shape)
    {
        var lib = new HandwrittenMonitorLock();
        using var watchdog = new CancellationTokenSource(Watchdog);
        using var holderReady = new ManualResetEventSlim(false);
        using var releaseHolder = new ManualResetEventSlim(false);
        using var thrown = new ManualResetEventSlim(false);

        var thrower = Task.Run(() =>
        {
            try
            {
                switch (shape)
                {
                    case ThrowShape.DirectLock:
                        lib.ThrowInsideDirectLockWhen(() =>
                        {
                            holderReady.Set();
                            releaseHolder.Wait(watchdog.Token);
                        });
                        break;
                    case ThrowShape.FormA:
                        lib.ThrowInsideFormAWhen(() =>
                        {
                            holderReady.Set();
                            releaseHolder.Wait(watchdog.Token);
                        });
                        break;
                    case ThrowShape.FormB:
                        lib.ThrowInsideFormBWhen(() =>
                        {
                            holderReady.Set();
                            releaseHolder.Wait(watchdog.Token);
                        });
                        break;
                }
            }
            catch (InvalidOperationException)
            {
                thrown.Set();
            }
        }, watchdog.Token);

        Assert.True(holderReady.Wait(TimeSpan.FromSeconds(10)), "thrower never acquired the lock");

        var probe = Task.Run(lib.ProbeAcquire, watchdog.Token);
        await Task.Delay(100, watchdog.Token);
        Assert.False(probe.IsCompleted, "the probe acquired while the gate was still held");

        releaseHolder.Set();
        await thrower.WaitAsync(watchdog.Token);
        Assert.True(thrown.IsSet);

        // The finally release (or lock unwind) must free the gate — the probe
        // completes instead of deadlocking the suite.
        await probe.WaitAsync(TimeSpan.FromSeconds(10), watchdog.Token);
        Assert.Equal(1, lib.ProbeCount);
    }

    // ------------------------------------------------------------------
    // Volatile stop flag: the spinning worker observes the stop.
    // ------------------------------------------------------------------

    [Fact]
    public async Task VolatileStopFlag_WorkerExitsAfterStopIsSet()
    {
        var worker = new VolatileStopFlag();
        using var watchdog = new CancellationTokenSource(Watchdog);

        var run = Task.Run(worker.RunUntilStopped, watchdog.Token);
        await Task.Delay(200, watchdog.Token);
        Assert.True(worker.Iterations > 0, "the spinning worker made no progress");

        worker.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(10), watchdog.Token);

        Assert.True(worker.Stopped);
        Assert.True(worker.Iterations > 0);
    }

    // ------------------------------------------------------------------
    // Recursive + nested lock behavior.
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecursiveLock_SameThreadReentersToFullDepth()
    {
        var counter = new Counter();
        using var watchdog = new CancellationTokenSource(Watchdog);

        await Task.Run(() => counter.RecursiveReentry(3), watchdog.Token).WaitAsync(watchdog.Token);

        Assert.Equal(4, counter.MaxReentryDepth);
        Assert.Equal(1, counter.LeafCalls);
    }

    [Fact]
    public async Task RecursiveLock_ConcurrentRecursion_AllLeavesReached()
    {
        var counter = new Counter();
        using var watchdog = new CancellationTokenSource(Watchdog);

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                counter.RecursiveReentry(2);
            }
        }, watchdog.Token)).ToArray();

        await Task.WhenAll(workers).WaitAsync(watchdog.Token);

        Assert.Equal(200, counter.LeafCalls);
        Assert.Equal(3, counter.MaxReentryDepth);
    }

    [Fact]
    public async Task NestedLock_ConcurrentIncrements_AreAllCounted()
    {
        var counter = new Counter();
        using var watchdog = new CancellationTokenSource(Watchdog);

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                counter.IncrementNested();
            }
        }, watchdog.Token)).ToArray();

        await Task.WhenAll(workers).WaitAsync(watchdog.Token);

        Assert.Equal(800, counter.NestedCount);
    }

    [Fact]
    public async Task HandwrittenForms_ConcurrentIncrements_AreAllCounted()
    {
        var lib = new HandwrittenMonitorLock();
        using var watchdog = new CancellationTokenSource(Watchdog);

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                lib.IncrementFormA();
                lib.IncrementFormB();
            }
        }, watchdog.Token)).ToArray();

        await Task.WhenAll(workers).WaitAsync(watchdog.Token);

        Assert.Equal(800, lib.FormACount);
        Assert.Equal(800, lib.FormBCount);
    }

    // ------------------------------------------------------------------
    // Out-of-scope contrast section: untouched, but behaves correctly.
    // ------------------------------------------------------------------

    [Fact]
    public void OutOfScopePrimitives_BehaveWithoutNormalization()
    {
        Assert.True(OutOfScopePrimitives.SemaphoreAcquireWithinMs());
        Assert.True(OutOfScopePrimitives.EventHandleSignalRoundtrip());
    }
}
