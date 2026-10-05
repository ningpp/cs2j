using SampleThreadStaticLib;
using Xunit;

namespace SampleThreadStaticApp;

/// <summary>
/// BOM hit-file behavior: the rewritten holder keeps normal per-thread storage.
/// </summary>
public sealed class BomCounterConcurrencyTests
{
    [Fact]
    public void TwoThreads_StayIsolated_AndFreshThreadSeesDefault()
    {
        var values = new int[2];
        var t1 = new Thread(() =>
        {
            BomCounter.Increment();
            values[0] = BomCounter.Current;
        });
        var t2 = new Thread(() =>
        {
            BomCounter.Increment();
            values[1] = BomCounter.Current;
        });
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();

        Assert.Equal(1, values[0]);
        Assert.Equal(1, values[1]);

        var observed = -1;
        var observer = new Thread(() => observed = BomCounter.Current);
        observer.Start();
        observer.Join();
        Assert.Equal(0, observed);
    }
}
