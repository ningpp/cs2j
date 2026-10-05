using SampleThreadStaticLib;
using Xunit;

namespace SampleThreadStaticApp;

/// <summary>
/// Case 4 behavior: cross-file sites rewrite to the same per-thread holders, so
/// writes performed through CrossFileUser stay isolated per thread and are
/// invisible to any other thread.
/// </summary>
[Collection("SharedPlainCounter")]
public sealed class CrossFileUserConcurrencyTests
{
    [Fact]
    public void CrossFileSites_AccumulatePerThread_AndStayIsolated()
    {
        const int threadCount = 4;
        var outcomes = new (int First, int PeekAfter, int Second)[threadCount];
        var threads = new Thread[threadCount];

        for (var i = 0; i < threadCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                // Fresh thread: Hits 0 + 5 = 5, Tally = 5 -> returns 10.
                var first = CrossFileUser.BumpAcrossFiles(5);
                var peekAfter = CrossFileUser.Peek();

                // Same thread: Hits 5 + 3 = 8, Tally = 3 -> returns 11.
                var second = CrossFileUser.BumpAcrossFiles(3);

                outcomes[index] = (first, peekAfter, second);
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

        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(10, outcome.First);
            Assert.Equal(10, outcome.PeekAfter);
            Assert.Equal(11, outcome.Second);
        });

        var observed = -1;
        var observer = new Thread(() => observed = CrossFileUser.Peek());
        observer.Start();
        observer.Join();
        Assert.Equal(0, observed);
    }
}
