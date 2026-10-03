using System.Threading;

namespace SampleConcurrencyLib;

/// <summary>
/// C29 out-of-scope contrast section (design §2.7 / CN-11): SemaphoreSlim and
/// EventWaitHandle hits are counted (CS2J2961, Info) and left byte-identical —
/// the pass must not touch, rewrite or residual-track these sites. 仅审计
/// 2961，树不动。
/// </summary>
public static class OutOfScopePrimitives
{
    public static bool SemaphoreAcquireWithinMs()
    {
        using var gate = new SemaphoreSlim(1, 1);
        return gate.Wait(20);
    }

    public static bool EventHandleSignalRoundtrip()
    {
        using var handle = new EventWaitHandle(false, EventResetMode.AutoReset);
        handle.Set();
        return handle.WaitOne(20);
    }
}
