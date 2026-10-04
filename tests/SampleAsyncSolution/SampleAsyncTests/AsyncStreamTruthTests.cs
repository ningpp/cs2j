using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SampleAsyncLib;
using Xunit;

namespace SampleAsyncTests;

/// <summary>
/// .NET-side truth oracle for the C38 async-stream fixture (design §7.2).
/// Every test pins a C# 8 / .NET (net10) behavioral truth about await-foreach
/// (ordering, cancellation visibility, in-flight vs normal-end double-fault
/// disposal) and await-using (null tolerance, LIFO release, exactly-once,
/// double-fault masking). The Java half of the equivalence contract is
/// compared against these outcomes; the same tests also run unchanged on the
/// async-normalize intermediate tree, proving the explicit expansion preserves
/// observable behavior.
///
/// The test project itself contains NO await-foreach / await-using syntax on
/// purpose: every async-stream consumption site lives in SampleAsyncLib, so
/// this file is not part of the residual/statistics surface.
/// </summary>
public class AsyncStreamTruthTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    // ----------------------------------------------------------------
    // T1: ordering over a real BCL channel source.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T1_ChannelSource_AwaitForeach_YieldsElementsInOrder()
    {
        // C# 8/.NET truth: consuming a bounded channel (capacity 1) with a
        // short producer delay — real producer/consumer scheduling — collects
        // exactly 1..N in order.
        const int n = 6;
        using var watchdog = new CancellationTokenSource(Watchdog);

        var bounded = ChannelProbe.StartBounded(n, delayMs: 15, watchdog.Token);
        var fromBounded = await ChannelProbe.CollectReaderAsync(bounded.Reader, watchdog.Token);
        await bounded.ProducerTask;
        Assert.Equal(Enumerable.Range(1, n), fromBounded);

        // The WrapReader helper exposes the same BCL iterator as a plain
        // IAsyncEnumerable; bare consumption gives the same ordered result.
        var unbounded = ChannelProbe.StartUnbounded(n, watchdog.Token);
        var fromWrapper = await ChannelProbe.CollectAsync(
            ChannelProbe.WrapReader(unbounded.Reader, watchdog.Token));
        await unbounded.ProducerTask;
        Assert.Equal(Enumerable.Range(1, n), fromWrapper);
    }

    // ----------------------------------------------------------------
    // T2: cancellation token visibility at GetAsyncEnumerator.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T2_WithCancellation_TokenReachesGetAsyncEnumerator_BarePassesNone()
    {
        // C# 8/.NET truth: WithCancellation(token) forwards that exact token
        // to GetAsyncEnumerator; a bare await-foreach forwards
        // CancellationToken.None (the compiler default argument).
        using var cts = new CancellationTokenSource();

        var withToken = new HandwrittenAsyncEnumerable(new[] { 1, 2, 3 });
        await HandwrittenAsyncEnumerable.ConsumeWithCancellationAsync(withToken, cts.Token);
        Assert.True(withToken.TokenRecorded);
        Assert.Equal(cts.Token, withToken.RecordedToken);

        var bare = new HandwrittenAsyncEnumerable(new[] { 1, 2, 3 });
        await HandwrittenAsyncEnumerable.ConsumeBareAsync(bare);
        Assert.True(bare.TokenRecorded);
        Assert.Equal(CancellationToken.None, bare.RecordedToken);
    }

    // ----------------------------------------------------------------
    // T3: cancellation surfaces OCE and DisposeAsync still runs.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T3a_PreCanceledToken_ThrowsOperationCanceled_AndDisposeStillRuns()
    {
        // C# 8/.NET truth: with a pre-canceled token, MoveNextAsync's OCE
        // escapes the await-foreach and DisposeAsync still runs exactly once
        // (release on the in-flight exception path).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new CancellationProbe(new[] { 1, 2, 3 });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CancellationProbe.ConsumeAsync(source, cts.Token));

        Assert.True(source.DisposeCalled);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task T3b_MidEnumerationCancel_FirstElementArrives_ThenThrows_AndDisposeRuns()
    {
        // C# 8/.NET truth: at least one element arrives, then cancellation is
        // requested; the following MoveNextAsync surfaces OCE and
        // DisposeAsync still runs.
        using var cts = new CancellationTokenSource();
        var source = new CancellationProbe(new[] { 1, 2, 3 });
        var received = new List<int>();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CancellationProbe.ConsumeAsync(source, cts.Token, value =>
            {
                received.Add(value);
                cts.Cancel();
            }));

        Assert.Contains(1, received);
        Assert.True(source.DisposeCalled);
        Assert.Equal(1, source.DisposeCount);
    }

    // ----------------------------------------------------------------
    // T4: double fault on the in-flight path — MEASURED .NET 10 TRUTH:
    // ordinary finally masking, the DisposeAsync exception wins.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T4_InFlightDoubleFault_DisposeExceptionMasksBody_AndDisposeRan()
    {
        // MEASURED .NET TRUTH (net10.0 / SDK 10.0.300): when the first
        // MoveNextAsync throws "body-in-flight" AND DisposeAsync throws
        // "dispose", await foreach surfaces the DisposeAsync exception
        // (ObjectDisposedException "dispose"): the compiler lowers await
        // foreach to an ordinary try/finally, and the finally fault masks
        // the in-flight fault. DisposeAsync still ran exactly once.
        //
        // This CONTRADICTS design §4.6 / D6 ("the in-flight main exception
        // wins; the dispose exception is discarded"): no such C# 8 special
        // case exists — verified across an 8-case matrix (MoveNext throws vs
        // body throws × Dispose throws vs not × sync vs async faults), every
        // cell followed plain try/finally semantics. The async-normalize pass
        // therefore DEFAULTS to the plain try/finally expansion
        // (FaithfulAsyncDisposeGuard=false), so this T4 passes identically on
        // the intermediate tree — AsyncStreamFixtureProjectTests pins the
        // 10/10 truth match. FaithfulAsyncDisposeGuard=true remains only as an
        // explicit opt-in whose semantics deliberately diverge from .NET.
        var source = new HandwrittenAsyncEnumerable(
            Array.Empty<int>(), throwOnFirstMoveNext: true, throwOnDispose: true);

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => DoubleFaultProbe.ConsumeInFlightForeachAsync(source));

        Assert.Equal("dispose", ex.ObjectName);
        Assert.True(source.DisposeCalled);
        Assert.Equal(1, source.DisposeCount);
    }

    // ----------------------------------------------------------------
    // T5: double fault on the normal-end path — dispose exception wins.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T5_NormalEndDisposeFault_DisposeExceptionPropagates()
    {
        // C# 8/.NET truth: the sequence exhausts normally and DisposeAsync
        // then throws; no main exception is in flight, so the dispose
        // exception surfaces (identical under plain-finally lowering and the
        // faithful-guard expansion).
        var source = new HandwrittenAsyncEnumerable(
            new[] { 1, 2, 3 }, throwOnFirstMoveNext: false, throwOnDispose: true);

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => DoubleFaultProbe.ConsumeNormalEndForeachAsync(source));

        Assert.Equal("dispose", ex.ObjectName);
        Assert.True(source.DisposeCalled);
        Assert.Equal(1, source.DisposeCount);
    }

    // ----------------------------------------------------------------
    // T6: await-using a null resource is a no-op.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T6_AwaitUsingNull_CompletesWithoutThrowing()
    {
        // C# 8/.NET truth: await-using tolerates a null reference (the
        // compiler emits a null guard); the scope returns normally, no NRE.
        await AsyncDisposalProbe.RunWithNullAsync();
    }

    // ----------------------------------------------------------------
    // T7: nested await-using releases in LIFO order.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T7_NestedAwaitUsing_ReleasesInLifoOrder()
    {
        // C# 8/.NET truth: nested await-using scopes (two declaration form +
        // one statement form, with a scheduling yield inside) release inner
        // first, then middle, then outer.
        var order = await LifoScope.RunAsync();

        Assert.Equal(new[] { "inner", "middle", "outer" }, order);
    }

    // ----------------------------------------------------------------
    // T8: await-using double fault — ordinary finally masking.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T8_AwaitUsing_BodyAndDisposeBothThrow_DisposeExceptionMasksBody()
    {
        // C# 8/.NET truth (design §4.6): await-using is ordinary try/finally
        // semantics — when the body throws "using-body" and DisposeAsync then
        // throws "using-dispose", the DisposeAsync exception masks the body
        // exception; DisposeAsync still ran exactly once.
        var resource = AsyncDisposalProbe.Create(
            "t8", new ObjectDisposedException("using-dispose"));

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => DoubleFaultProbe.RunThrowingBodyUsingAsync(resource));

        Assert.Equal("using-dispose", ex.ObjectName);
        Assert.True(resource.DisposeCalled);
        Assert.Equal(1, resource.DisposeCount);
    }

    // ----------------------------------------------------------------
    // T9: DisposeAsync exactly once on normal / throwing / canceled paths.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T9_AwaitUsing_DisposeAsyncExactlyOnce_NormalThrowingAndCanceledPaths()
    {
        // C# 8/.NET truth: DisposeAsync runs exactly once on the normal path,
        // on the body-exception path, and when the scope is entered after a
        // pre-canceled token was observed and handled.
        var normal = AsyncDisposalProbe.Create("normal");
        await AsyncDisposalProbe.RunNormalBodyAsync(normal);
        Assert.Equal(1, normal.DisposeCount);

        var throwing = AsyncDisposalProbe.Create("throwing-body");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DoubleFaultProbe.RunThrowingBodyUsingAsync(throwing));
        Assert.Equal(1, throwing.DisposeCount);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var canceled = AsyncDisposalProbe.Create("canceled-context");
        await AsyncDisposalProbe.RunAfterCancellationObservedAsync(cts.Token, canceled);
        Assert.Equal(1, canceled.DisposeCount);
    }

    // ----------------------------------------------------------------
    // T11: async-iterator producer — ordered delayed yields, CT merge.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T11_AsyncIterator_RangeDelayed_YieldsInOrder_AndMergedTokenCancels()
    {
        // C# 8/.NET truth: an async IAsyncEnumerable producer with real awaits
        // between yields produces 0..N-1 in order; the token passed to
        // GetAsyncEnumerator (EnumeratorCancellation merge) reaches Task.Delay:
        // a pre-canceled token surfaces OperationCanceledException from the
        // first MoveNextAsync.
        var collected = await CollectAsync(YieldProbe.RangeDelayed(6, 1, CancellationToken.None));
        Assert.Equal(Enumerable.Range(0, 6), collected);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var enumerator = YieldProbe.RangeDelayed(6, 1).GetAsyncEnumerator(cts.Token);
        try
        {
            // No async lambda here (the test project stays off the C18
            // closure-lift surface): ValueTask.AsTask() gives a plain Task.
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => enumerator.MoveNextAsync().AsTask());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    // ----------------------------------------------------------------
    // T12: async-iterator producer — yield break terminates early.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T12_AsyncIterator_UntilBreak_YieldsPrefixThenStops()
    {
        // C# 8/.NET truth: yield break ends the sequence exactly after the
        // boundary element — MoveNextAsync reports false, nothing follows.
        var collected = await CollectAsync(YieldProbe.UntilBreak(2));

        Assert.Equal(new[] { 0, 1, 2 }, collected);
    }

    // ----------------------------------------------------------------
    // T13: async-iterator try/finally — finally runs on exhaustion.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T13_AsyncIterator_FinallyRunsOnNormalExhaustion_ExactlyOnce()
    {
        // C# 8/.NET truth: full enumeration of a try/finally iterator runs the
        // finally exactly once when the sequence exhausts (DisposeAsync from
        // the consumer's end-of-loop path).
        YieldProbe.ResetCounters();

        var collected = await CollectAsync(YieldProbe.BoundedWithFinally(4));

        Assert.Equal(new[] { 0, 1, 2, 3 }, collected);
        Assert.Equal(1, YieldProbe.EnterCount);
        Assert.Equal(1, YieldProbe.FinallyCount);
    }

    // ----------------------------------------------------------------
    // T14: async-iterator try/finally — early DisposeAsync runs finally.
    // ----------------------------------------------------------------

    [Fact]
    public async Task T14_AsyncIterator_EarlyDispose_RunsFinallyExactlyOnce()
    {
        // C# 8/.NET truth: abandoning an iterator after two elements and
        // calling DisposeAsync explicitly runs the finally exactly once, even
        // though the sequence never exhausted.
        YieldProbe.ResetCounters();
        var enumerator = YieldProbe.BoundedWithFinally(4).GetAsyncEnumerator(CancellationToken.None);
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(0, enumerator.Current);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(1, enumerator.Current);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        Assert.Equal(1, YieldProbe.EnterCount);
        Assert.Equal(1, YieldProbe.FinallyCount);
    }

    // ----------------------------------------------------------------
    // Manual async-stream driver — keeps await foreach/await using out of
    // this test project (only SampleAsyncLib is the statistics surface).
    // ----------------------------------------------------------------

    private static async Task<List<int>> CollectAsync(
        IAsyncEnumerable<int> source, CancellationToken cancellationToken = default)
    {
        var items = new List<int>();
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                items.Add(enumerator.Current);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        return items;
    }
}
