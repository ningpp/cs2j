using SampleThreadStaticLib;
using Xunit;

namespace SampleThreadStaticApp;

/// <summary>
/// Case 1 behavior: the uninitialized counter has an independent per-thread
/// copy; 1000 increments on each of four threads end at 1000 on every thread
/// and a fresh thread still observes default(int).
/// </summary>
[Collection("SharedPlainCounter")]
public sealed class PlainCounterConcurrencyTests
{
    [Fact]
    public void FourThreads_EachThousandIncrements_StayIsolated()
    {
        const int threadCount = 4;
        const int iterations = 1000;

        var finals = new int[threadCount];
        var threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                for (var n = 0; n < iterations; n++)
                {
                    PlainCounter.Increment();
                }

                finals[index] = PlainCounter.Current;
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.All(finals, value => Assert.Equal(iterations, value));

        var observed = -1;
        var observer = new Thread(() => observed = PlainCounter.Current);
        observer.Start();
        observer.Join();
        Assert.Equal(0, observed);
    }
}
