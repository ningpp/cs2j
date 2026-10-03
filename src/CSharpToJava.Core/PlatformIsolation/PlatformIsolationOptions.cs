namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>隔离动作模式（设计 §4 组件表 / §3.5）。默认 Report。</summary>
public enum PlatformIsolationMode
{
    Report,
    Reject,
    Stub,
    Facade,
}

/// <summary>
/// platform-isolation 通道选项（设计 §4 组件表）。M1 只消费 <see cref="Mode"/>、
/// <see cref="Strict"/>、<see cref="ConfigPath"/>；三个 Enable* 为里程碑位
/// （M2/M3 的 Stub/Facade 生成开关），M1 仅声明不产生行为。
/// </summary>
public sealed class PlatformIsolationOptions
{
    public PlatformIsolationMode Mode { get; init; } = PlatformIsolationMode.Report;

    /// <summary>strict：Warning 级站点/抑制条目升级为 Error（设计 §3.5）。</summary>
    public bool Strict { get; init; }

    /// <summary>显式配置路径；为空时按源根 CWD 向上查找 Cs2jPlatformIsolation.json。</summary>
    public string? ConfigPath { get; init; }

    /// <summary>里程碑位（M2）：OS 探针调用点重定向开关。</summary>
    public bool EnableProbeStub { get; init; }

    /// <summary>里程碑位（M2）：PNSE 抛错成员桩开关。</summary>
    public bool EnableThrowStub { get; init; }

    /// <summary>里程碑位（M3）：Facade 端口生成开关。</summary>
    public bool EnableFacade { get; init; }
}