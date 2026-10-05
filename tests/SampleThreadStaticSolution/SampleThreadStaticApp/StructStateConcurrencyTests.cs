using SampleThreadStaticLib;
using Xunit;

namespace SampleThreadStaticApp;

/// <summary>
/// Case 3 behavior: the mutable struct thread-static copy is independent per
/// thread and statement-level temp lifting writes both the method mutation and
/// the field mutation back, so mutations persist across calls on the same thread.
/// </summary>
public sealed class StructStateConcurrencyTests
{
    [Fact]
    public void PerThreadCopies_Independent_AndMutationsPersist()
    {
        const int threadCount = 4;
        var snapshots = new (int X, int Y)[threadCount];
        var threads = new Thread[threadCount];

        for (var i = 0; i < threadCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                StructState.BumpCurrent();
                StructState.BumpCurrent();
                var snapshot = StructState.Snapshot();
                snapshots[index] = (snapshot.X, snapshot.Y);
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

        // One BumpCurrent: Bump() adds (1, 2), then X += 10 -> (11, 2).
        // Two calls -> (22, 4). The second call starting from (11, 2) proves
        // the TS-5 write-back persisted on that thread's own copy.
        Assert.All(snapshots, snapshot =>
        {
            Assert.Equal(22, snapshot.X);
            Assert.Equal(4, snapshot.Y);
        });

        var fresh = new CounterVec { X = -1, Y = -1 };
        var observer = new Thread(() => fresh = StructState.Snapshot());
        observer.Start();
        observer.Join();
        Assert.Equal(0, fresh.X);
        Assert.Equal(0, fresh.Y);
    }
}
