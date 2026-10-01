namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06 unsafe-ffm 预处理 pass 选项（设计 §4）。Enable* 是里程碑分段开关：
/// 已交付的规则段随 pass 开启，未交付段关闭。M0 交付 R1 unsafe 上下文
/// 剥除（unsafe 段是 pass 基座，无独立开关）；M1 交付 fixed 规则段
/// （R2 fixed 字段缓冲区、R3 fixed 语句），EnableFixed 默认开启，
/// 随 pass 开启；stackalloc/pointer/extern 规则属后续里程碑，仍关闭。
/// </summary>
public sealed class UnsafeFfmOptions
{
    /// <summary>M1 已交付，随 pass 开启：R2 fixed 字段 / R3 fixed 语句改写。</summary>
    public bool EnableFixed { get; init; } = true;

    /// <summary>M2：显式/隐式 stackalloc 统一堆化（M0 未交付）。</summary>
    public bool EnableStackAlloc { get; init; }

    /// <summary>M3：指针签名/指针表达式归一（M0 未交付）。</summary>
    public bool EnablePointer { get; init; }

    /// <summary>M4：extern/DllImport/LibraryImport 白名单归一（M0 未交付）。</summary>
    public bool EnableExtern { get; init; }

    /// <summary>M4：extern FFM 白名单配置路径（null = 无白名单，extern 全部 UF060）。</summary>
    public string? ExternWhitelistPath { get; init; }

    /// <summary>strict 下可升级诊断为 Error（设计 §6）。</summary>
    public bool Strict { get; init; } = true;

    /// <summary>
    /// 单文件模式：跨文件分析发 UF090 跳过（项目适配器始终整树编译，传 false）。
    /// </summary>
    public bool SingleFileMode { get; init; }
}
