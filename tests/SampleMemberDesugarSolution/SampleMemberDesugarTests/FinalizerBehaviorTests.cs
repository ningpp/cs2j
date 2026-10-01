using System;
using System.Runtime.CompilerServices;
using SampleMemberDesugarLib;
using Xunit;

namespace SampleMemberDesugarTests;

/// <summary>
/// C39 终结器消除行为闸门（设计 §7.4）：在 A 档（仅报告，终结器全部保留）与
/// B 档（安全形态删除、危险形态保留）的中间树产物上同样通过。
/// </summary>
public class FinalizerBehaviorTests
{
    [Fact]
    public void Dispose_pattern_keeps_observable_dispose_semantics()
    {
        var target = new FinalizerDisposePattern();
        Assert.False(target.IsDisposed);

        target.Dispose();

        Assert.True(target.IsDisposed);

        // 重复 Dispose 幂等。
        target.Dispose();
        Assert.True(target.IsDisposed);
    }

    [Fact]
    public void Derived_instance_collection_runs_base_finalizer()
    {
        FinalizerBaseWithFinalizer.BaseFinalizerRuns = 0;
        CreateAndDropDerived();

        // 强制回收 + 终结队列，自旋等待基类终结器（不断言派生空终结器是否执行；
        // B 档下派生空终结器会被删除，基类 Other 终结器 5204 保留，行为不变）。
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (FinalizerBaseWithFinalizer.BaseFinalizerRuns == 0
               && DateTime.UtcNow < deadline)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.True(FinalizerBaseWithFinalizer.BaseFinalizerRuns >= 1,
            "派生实例被回收后基类终结器必须至少执行一次");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAndDropDerived()
    {
        var derived = new FinalizerDerived();
        GC.KeepAlive(derived);
    }
}
