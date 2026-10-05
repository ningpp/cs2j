using SampleThreadStaticLib;
using Xunit;

namespace SampleThreadStaticApp;

/// <summary>
/// Case 5 behavior: sites inside the lambda and sites in ordinary methods share
/// the same per-thread storage; four threads observe identical isolated results
/// and a fresh thread still sees the default.
/// </summary>
public sealed class LambdaSharedConcurrencyTests
{
    [Fact]
    public void LambdaAndMethodSites_SharePerThreadStorage()
    {
        const int threadCount = 4;
        var outcomes = new (int FirstReturn, int CurrentAfter)[threadCount];
        var threads = new Thread[threadCount];

        for (var i = 0; i < threadCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                // Tally 0 -> 10, then lambda reads Tally(10) + 10 = 20.
                var firstReturn = LambdaShared.ViaLambda(10);
                var currentAfterFirst = LambdaShared.Current;

                // Same thread: Tally 10 -> 15, lambda reads 15 + 5 = 20.
                LambdaShared.ViaLambda(5);

                outcomes[index] = (firstReturn, currentAfterFirst);
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
            Assert.Equal(20, outcome.FirstReturn);
            Assert.Equal(10, outcome.CurrentAfter);
        });

        var observed = -1;
        var observer = new Thread(() => observed = LambdaShared.Current);
        observer.Start();
        observer.Join();
        Assert.Equal(0, observed);
    }
}
