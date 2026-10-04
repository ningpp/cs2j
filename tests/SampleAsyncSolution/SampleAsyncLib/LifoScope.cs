using System.Collections.Generic;
using System.Threading.Tasks;

namespace SampleAsyncLib;

/// <summary>
/// C38 fixture: three nested await-using scopes — two declaration-form
/// (<c>await using var …</c>) and one statement-form
/// (<c>await using (…) { … }</c>) — over named <see cref="AsyncDisposalProbe"/>
/// resources. The release log must read inner → middle → outer (LIFO).
/// </summary>
public static class LifoScope
{
    /// <summary>
    /// Runs the three nested scopes in a separate async method and snapshots
    /// the release order only after THAT method's state machine has completed:
    /// declaration-form await-using resources are disposed when their owning
    /// scope exits, so after awaiting the scope method all three disposals
    /// (inner statement-form first, then middle/outer declaration-form) have
    /// been logged.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        AsyncDisposalProbe.ResetOrder();

        await RunScopesAsync();

        return AsyncDisposalProbe.SnapshotOrder();
    }

    private static async Task RunScopesAsync()
    {
        await using var outer = AsyncDisposalProbe.Create("outer");
        await using var middle = AsyncDisposalProbe.Create("middle");
        await using (AsyncDisposalProbe.Create("inner"))
        {
            // A real scheduling yield inside the innermost scope (Task.Delay
            // rather than Task.Yield: YieldAwaitable is outside the C18
            // lightweight awaitable set; Delay(1) is a genuine scheduler hop).
            await Task.Delay(1);
        }
    }
}
