namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06 unsafe-ffm 预处理 pass 选项（设计 §4）。Enable* 是里程碑分段开关：
/// 已交付的规则段随 pass 开启，未交付段关闭。M0 交付 R1 unsafe 上下文
/// 剥除（unsafe 段是 pass 基座，无独立开关）；M1 交付 fixed 规则段
/// （R2 fixed 字段缓冲区、R3 fixed 语句），EnableFixed 默认开启，
/// 随 pass 开启；M2 已交付 stackalloc 规则段（R4 显式/隐式 stackalloc
/// 统一堆化），EnableStackAlloc 默认开启；M3 已交付 pointer 规则段
/// （R5 指针签名白名单 + R6 表达式索引化，含 M3b 算术/比较/差值族），
/// EnablePointer 默认开启；M4 已交付 R7 用户 ref struct 声明处置
/// （带 ref-only 成员者 UF050 拒绝、纯数据型 UF052 去 ref 降级）、
/// R9 ref struct using 归一（UF055）、InlineArray 盘点
/// （UF051）——ref struct 段是 pass 基座，与 R1 同级语义，无独立开关；
/// R8 extern 互操作显式拒绝（UF060/UF070）随 EnableExtern 开启。
/// M4 裁决：白名单 facade 归一作为独立 opt-in 子段交付——
/// <see cref="Whitelist"/> 非 null 时，命中的 extern 归一为受控
/// [Cs2j.Generation.InteropFacade] 声明（C# 半区响亮 stub、Java 半区
/// FFM 发射通道消费）；其余 extern 仍默认 UF060。
/// </summary>
public sealed class UnsafeFfmOptions
{
    /// <summary>M1 已交付，随 pass 开启：R2 fixed 字段 / R3 fixed 语句改写。</summary>
    public bool EnableFixed { get; init; } = true;

    /// <summary>M2 已交付，随 pass 开启：R4 显式/隐式 stackalloc 统一堆化。</summary>
    public bool EnableStackAlloc { get; init; } = true;

    /// <summary>M3 已交付，随 pass 开启：R5 指针签名 + R6 表达式索引化。</summary>
    public bool EnablePointer { get; init; } = true;

    /// <summary>
    /// M4 已交付，随 pass 开启：R8 extern-like（extern 修饰符或
    /// [DllImport]/[LibraryImport]）→ UF060；Marshal/NativeLibrary/
    /// SafeHandle/HandleRef 站点 → UF070。
    /// </summary>
    public bool EnableExtern { get; init; } = true;

    /// <summary>
    /// M4 白名单 opt-in 子段（已交付）：extern FFM 白名单。null（默认）=
    /// 无白名单，extern 全部 UF060；非 null 时命中条目归一为受控 interop
    /// facade（InteropFacade 标记 + 响亮 stub 体），由 Java 半区 FFM 发射
    /// 通道消费。由 CLI <c>--extern-whitelist &lt;path&gt;</c> 加载。
    /// </summary>
    public ExternWhitelist? Whitelist { get; init; }

    /// <summary>
    /// 白名单文件路径（诊断/日志用途）：实际消费的是 <see cref="Whitelist"/>，
    /// 由适配器在装配期加载并注入。
    /// </summary>
    public string? ExternWhitelistPath { get; init; }

    /// <summary>strict 下可升级诊断为 Error（设计 §6）。</summary>
    public bool Strict { get; init; } = true;

    /// <summary>
    /// 单文件模式：跨文件分析发 UF090 跳过（项目适配器始终整树编译，传 false）。
    /// </summary>
    public bool SingleFileMode { get; init; }
}
