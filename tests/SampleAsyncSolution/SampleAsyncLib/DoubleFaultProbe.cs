using System;
using System.Threading.Tasks;

namespace SampleAsyncLib;

/// <summary>
/// C38 fixture: the double-fault timing oracle (design §4.6). Each method
/// lets the exception escape so callers observe which fault wins.
/// <list type="bullet">
/// <item>await-foreach whose first MoveNextAsync faults AND whose
/// DisposeAsync faults (in-flight path).</item>
/// <item>await-foreach that exhausts normally and whose DisposeAsync then
/// faults (normal-completion path).</item>
/// <item>await-using whose body throws AND whose DisposeAsync throws
/// (ordinary try/finally semantics).</item>
/// </list>
/// </summary>
public static class DoubleFaultProbe
{
    /// <summary>
    /// The enumerator fails MoveNextAsync with "body-in-flight" and fails
    /// DisposeAsync with "dispose". MEASURED .NET 10 TRUTH: await foreach is
    /// lowered to an ordinary try/finally, so the finally's DisposeAsync
    /// exception MASKS the in-flight exception ("dispose" surfaces);
    /// DisposeAsync still runs exactly once. (Design §4.6 / D6 predicted the
    /// opposite; T4 pins the measured behavior.)
    /// </summary>
    public static async Task ConsumeInFlightForeachAsync(HandwrittenAsyncEnumerable source)
    {
        await foreach (var value in source)
        {
            _ = value;
        }
    }

    /// <summary>
    /// The sequence exhausts normally (MoveNextAsync → false) and
    /// DisposeAsync then fails with "dispose". No main exception is in
    /// flight, so the dispose exception surfaces on the normal-completion
    /// path (under both plain-finally semantics and the guard expansion).
    /// </summary>
    public static async Task ConsumeNormalEndForeachAsync(HandwrittenAsyncEnumerable source)
    {
        await foreach (var value in source)
        {
            _ = value;
        }
    }

    /// <summary>
    /// The await-using body throws InvalidOperationException("using-body")
    /// and DisposeAsync throws ObjectDisposedException("using-dispose").
    /// Await-using is ordinary try/finally: the release exception masks the
    /// body exception.
    /// </summary>
    public static async Task RunThrowingBodyUsingAsync(AsyncDisposalProbe resource)
    {
        await using (resource)
        {
            throw new InvalidOperationException("using-body");
        }
    }
}
