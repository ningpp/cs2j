using SampleThreadStaticLib;
using Xunit;

namespace SampleThreadStaticApp;

/// <summary>
/// Case 2 behavior (FirstThreadFaithful, the default policy): the inline
/// initializer is visible only to the thread that triggers type initialization;
/// a barrier guarantees the designated first thread completes its first touch
/// before any later thread reads, and every later thread sees default(int)=0.
/// </summary>
public sealed class InitializedCounterFaithfulTests
{
    [Fact]
    public void FirstTouchingThread_SeesInitializer_AllLaterThreads_SeeDefault()
    {
        var firstTouched = new ManualResetEventSlim(false);
        var firstValue = -1;

        // A dedicated (non-pool) thread performs the very first static access of
        // the type, so its thread runs the synthesized static constructor.
        var first = new Thread(() =>
        {
            firstValue = InitializedCounter.Read();
            firstTouched.Set();
        });
        first.Start();
        Assert.True(firstTouched.Wait(TimeSpan.FromSeconds(30)));
        first.Join();
        Assert.Equal(42, firstValue);

        const int laterCount = 4;
        var barrier = new Barrier(laterCount);
        var laterValues = new int[laterCount];
        var laterThreads = new Thread[laterCount];
        for (var i = 0; i < laterCount; i++)
        {
            var index = i;
            laterThreads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                laterValues[index] = InitializedCounter.Read();
            });
        }

        foreach (var thread in laterThreads)
        {
            thread.Start();
        }

        foreach (var thread in laterThreads)
        {
            thread.Join();
        }

        Assert.All(laterValues, value => Assert.Equal(0, value));
    }
}
