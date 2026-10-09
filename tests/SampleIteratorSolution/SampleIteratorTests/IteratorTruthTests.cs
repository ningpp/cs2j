using System.Collections;
using System.Threading;
using SampleIteratorLib;
using Xunit;

namespace SampleIteratorTests;

/// <summary>
/// C04 M2 .NET 真身预言机（设计 §7.1 第 5 组）：
/// ① 枚举到一半 Dispose() 后 finally 恰好一次；
/// ② 正常枚举完 finally 恰好一次；
/// ③ 重复枚举互不共享状态。
/// 另覆盖 List&lt;T&gt;/数组 foreach、嵌套 finally 三路径、提前结束迭代、
/// foreach-in-try、非泛型 IEnumerable，以及 M4 含 yield 的 switch
/// （多 section/default、循环内 switch、空 section 贯穿）。这些测试同时在
/// 原始树与 iterator-lowering 中间树上运行——状态机改写必须保持全部 C# 语义。
/// </summary>
public sealed class IteratorTruthTests
{
    private static List<int> ToList(IEnumerable<int> values)
    {
        var result = new List<int>();
        foreach (var v in values)
        {
            result.Add(v);
        }
        return result;
    }

    [Fact]
    public void T01_Range_ProducesExpectedSequence()
    {
        var source = new SequenceSource();
        Assert.Equal(new[] { 5, 6, 7 }, ToList(source.Range(5, 3)));
    }

    [Fact]
    public void T02_Evens_ListForeach_StructEnumerator()
    {
        var source = new SequenceSource();
        Assert.Equal(new[] { 2, 4, 6 },
            ToList(source.Evens(new List<int> { 1, 2, 3, 4, 5, 6 })));
    }

    [Fact]
    public void T03_Tens_ArrayForeach()
    {
        var source = new SequenceSource();
        Assert.Equal(new[] { 10, 20, 30 }, ToList(source.Tens(new[] { 1, 2, 3 })));
    }

    [Fact]
    public void T04_Guarded_FullEnumeration_FinallyExactlyOnce()
    {
        var source = new SequenceSource();
        Assert.Equal(new[] { 1, 2, 3 }, ToList(source.Guarded(0)));
        Assert.Equal(1, source.GuardFinally);
    }

    [Fact]
    public void T05_Guarded_YieldBreak_FinallyExactlyOnce()
    {
        var source = new SequenceSource();
        Assert.Equal(new[] { 1, 2 }, ToList(source.Guarded(1)));
        Assert.Equal(1, source.GuardFinally);
    }

    [Fact]
    public void T06_Guarded_EarlyDisposeHalfWay_FinallyExactlyOnce_EnumeratorDead_Idempotent()
    {
        var source = new SequenceSource();
        var enumerator = source.Guarded(0).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, enumerator.Current);
        Assert.True(enumerator.MoveNext());
        Assert.Equal(0, source.GuardFinally);

        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, source.GuardFinally);

        // 重复 Dispose 幂等；之后 MoveNext 立即返回 false。
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, source.GuardFinally);
        Assert.False(enumerator.MoveNext());
        Assert.Equal(1, source.GuardFinally);
    }

    [Fact]
    public void T07_DisposeBeforeFirstMoveNext_DoesNotRunFinally()
    {
        var source = new SequenceSource();
        var enumerator = source.Guarded(0).GetEnumerator();
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(0, source.GuardFinally);
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void T08_NestedFinally_FullEnumeration_BothOnce()
    {
        var source = new SequenceSource();
        Assert.Equal(new[] { 1, 2, 3, 4 }, ToList(source.Nested(0)));
        Assert.Equal(1, source.InnerFinally);
        Assert.Equal(1, source.OuterFinally);
    }

    [Fact]
    public void T09_NestedFinally_YieldBreak_BothOnce()
    {
        var source = new SequenceSource();
        Assert.Equal(new[] { 1, 2 }, ToList(source.Nested(1)));
        Assert.Equal(1, source.InnerFinally);
        Assert.Equal(1, source.OuterFinally);
    }

    [Fact]
    public void T10_NestedFinally_EarlyDisposeInsideInner_BothOnce()
    {
        var source = new SequenceSource();
        var enumerator = source.Nested(1).GetEnumerator();
        enumerator.MoveNext();
        enumerator.MoveNext();
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, source.InnerFinally);
        Assert.Equal(1, source.OuterFinally);
    }

    [Fact]
    public void T11_NestedFinally_EarlyDisposeInOuterOnly_OuterOnce()
    {
        var source = new SequenceSource();
        var enumerator = source.Nested(0).GetEnumerator();
        enumerator.MoveNext();
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(0, source.InnerFinally);
        Assert.Equal(1, source.OuterFinally);
    }

    [Fact]
    public void T12_ListInTry_NormalAndEarlyDispose()
    {
        var full = new SequenceSource();
        Assert.Equal(new[] { 7, 8, 9 },
            ToList(full.ListInTry(new List<int> { 7, 8, 9 })));
        Assert.Equal(1, full.GuardFinally);

        var early = new SequenceSource();
        var enumerator = early.ListInTry(new List<int> { 7, 8, 9 }).GetEnumerator();
        enumerator.MoveNext();
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, early.GuardFinally);
    }

    [Fact]
    public void T13_RepeatedEnumerations_DoNotShareState()
    {
        var source = new SequenceSource();
        var first = source.Guarded(0).GetEnumerator();
        var second = source.Guarded(0).GetEnumerator();

        Assert.True(first.MoveNext());
        Assert.Equal(1, first.Current);
        Assert.True(second.MoveNext());
        Assert.Equal(1, second.Current);
        Assert.True(first.MoveNext());
        Assert.Equal(2, first.Current);
        Assert.True(second.MoveNext());
        Assert.Equal(2, second.Current);

        // 交错推进后各自继续独立走完剩余序列。
        Assert.True(first.MoveNext());
        Assert.Equal(3, first.Current);
        Assert.False(first.MoveNext());
        Assert.True(second.MoveNext());
        Assert.Equal(3, second.Current);
        Assert.False(second.MoveNext());
        Assert.Equal(2, source.GuardFinally);
    }

    [Fact]
    public void T14_Mixed_NonGenericEnumerable()
    {
        var source = new SequenceSource();
        var values = new List<object?>();
        foreach (var v in source.Mixed())
        {
            values.Add(v);
        }
        Assert.Equal(new object?[] { 1, "two" }, values);
    }

    // ── C04 M3：using 语句/声明/嵌套 LIFO/提前 Dispose ──

    [Fact]
    public void T15_UsingBlock_FullEnumeration_DisposesExactlyOnce()
    {
        var resource = new CountingResource();
        var source = new SequenceSource();
        Assert.Equal(new[] { 11, 22 }, ToList(source.UsingBlock(resource)));
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public void T16_UsingBlock_EarlyDisposeHalfWay_DisposesOnce_Idempotent()
    {
        var resource = new CountingResource();
        var enumerator = new SequenceSource().UsingBlock(resource).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(0, resource.DisposeCount);
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, resource.DisposeCount);
        ((IDisposable)enumerator).Dispose();
        Assert.Equal(1, resource.DisposeCount);
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void T17_UsingDeclaration_DisposesAtBlockEnd()
    {
        var resource = new CountingResource();
        Assert.Equal(new[] { 33, 44 },
            ToList(new SequenceSource().UsingDeclaration(resource)));
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public void T18_NestedUsing_DisposesLifo()
    {
        var log = new List<string>();
        Assert.Equal(new[] { 55 }, ToList(new SequenceSource().UsingLifo(log)));
        Assert.Equal(new[] { "b", "a" }, log);
    }

    [Fact]
    public void T19_Lock_HeldAcrossYields_ReleasedOnCompletion()
    {
        var gate = new object();
        var enumerator = new SequenceSource().Locked(gate).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.True(Monitor.IsEntered(gate));
        Assert.True(enumerator.MoveNext());
        Assert.True(Monitor.IsEntered(gate));
        Assert.False(enumerator.MoveNext());
        Assert.False(Monitor.IsEntered(gate));
    }

    [Fact]
    public void T20_Lock_ReleasedOnEarlyDispose()
    {
        var gate = new object();
        var enumerator = new SequenceSource().Locked(gate).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.True(Monitor.IsEntered(gate));
        ((IDisposable)enumerator).Dispose();
        Assert.False(Monitor.IsEntered(gate));
    }

    // ── C04 M4：含 yield 的 switch 展平后保持分支语义 ──

    [Fact]
    public void T21_SwitchMap_DispatchesBySection()
    {
        var source = new SequenceSource();
        Assert.Equal(new[] { 10 }, ToList(source.SwitchMap(1)));
        Assert.Equal(new[] { 20, 21 }, ToList(source.SwitchMap(2)));
        Assert.Equal(new[] { 99 }, ToList(source.SwitchMap(7)));
    }

    [Fact]
    public void T22_SwitchInLoop_EmptySectionFallthrough_AndDefault()
    {
        var source = new SequenceSource();
        Assert.Equal(new[] { 0, 100, -2, -3 },
            ToList(source.SwitchInLoop(new[] { 0, 1, 2, 3 })));
    }
}
