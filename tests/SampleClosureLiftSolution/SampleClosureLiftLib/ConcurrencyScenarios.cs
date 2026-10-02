namespace SampleClosureLiftLib;

/// <summary>
/// Cross-thread shared-storage equivalence (design §7.2 tail): two threads
/// increment the SAME captured cell through one delegate, in a strictly
/// alternating event-driven schedule. The result is deterministic; the events
/// are the test harness's own synchronization — closure-lift must not add any.
/// </summary>
public static class ConcurrencyScenarios
{
    public static int PingPongCounter(int rounds)
    {
        var count = 0;
        Action inc = () => count++;

        using var aTurn = new AutoResetEvent(initialState: false);
        using var bTurn = new AutoResetEvent(initialState: false);

        var a = new Thread(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                aTurn.WaitOne();
                inc();
                bTurn.Set();
            }
        });

        var b = new Thread(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                bTurn.WaitOne();
                inc();
                aTurn.Set();
            }
        });

        a.Start();
        b.Start();
        aTurn.Set();
        a.Join();
        b.Join();

        return count; // exactly 2 * rounds
    }
}
