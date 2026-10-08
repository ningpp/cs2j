namespace CSharpToJava.Core.IteratorLowering.ControlFlow;

/// <summary>
/// 词法上包含 yield 挂起点、需要在 MoveNext/Dispose 两处共享清理逻辑的
/// 保护区域。M2 覆盖两种：用户 try-finally（<see cref="RegionKind.UserFinally"/>）
/// 与可 Dispose foreach 枚举器（<see cref="RegionKind.ForeachDispose"/>）。
/// </summary>
/// <param name="Id">成员内唯一序号（语法序）。</param>
/// <param name="ParentId">词法外层活动区域；null 表示顶层。</param>
/// <param name="Kind">区域种类。</param>
/// <param name="FirstState">区域内首个 yield return 的挂起状态号（含）。</param>
/// <param name="LastState">区域内末个 yield return 的挂起状态号（含）。</param>
/// <param name="EnumeratorField">foreach 区域：被提升的枚举器字段；用户 finally 为 null。</param>
public sealed record RegionPlan(
    int Id,
    int? ParentId,
    RegionKind Kind,
    int FirstState,
    int LastState,
    CaptureField? EnumeratorField)
{
    /// <summary>状态 s 是否表示一次落在本区域内的挂起。</summary>
    public bool ContainsState(int state) => state >= FirstState && state <= LastState;
}

public enum RegionKind
{
    /// <summary>用户 try-finally：finally 块原样搬运，进出区域按区间守卫。</summary>
    UserFinally,

    /// <summary>可 Dispose 的 foreach 枚举器：finally 合成枚举器 Dispose。</summary>
    ForeachDispose,
}
