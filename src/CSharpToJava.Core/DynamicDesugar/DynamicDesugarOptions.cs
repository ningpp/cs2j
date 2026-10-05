namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>dynamic 去糖动作模式（设计 §4.6）。M1 仅交付 <see cref="Audit"/> 行为。</summary>
public enum DynamicMode
{
    /// <summary>纯审计：源码字节不变，产出具名诊断 + 报告 JSON（M1 唯一完整模式）。</summary>
    Audit,

    /// <summary>可证明子集具体化重写（M2 交付；M1 选择此值时分级按 rewrite 口径，仍不重写）。</summary>
    Rewrite,
}

/// <summary>
/// dynamic-desugar 通道选项（设计 §4.6）。全部 init，风格对齐 PlatformIsolationOptions。
/// </summary>
public sealed class DynamicDesugarOptions
{
    /// <summary>运行模式；默认 Audit（D1：审计先行、重写长期 opt-in）。</summary>
    public DynamicMode Mode { get; init; } = DynamicMode.Audit;

    /// <summary>strict：Warning 站点（含 manifest 豁免）升级 Error（D3）。</summary>
    public bool Strict { get; init; }

    /// <summary>
    /// 审计登记 manifest 显式路径（Cs2jDynamicAudit.json，D13）；null 时按约定查找，
    /// 缺省视为无 manifest。M1 不读取本字段（manifest 为 M2 文件）。
    /// </summary>
    public string? AuditManifestPath { get; init; }

    /// <summary>P1 证明仅接受 sealed T（D2/D11）；M2 恒 true，M3 放宽时改 false。</summary>
    public bool SealedOnly { get; init; } = true;

    /// <summary>A2 显式强转插入开关（D10，M3 观察项）；M2 不读取其 true 路径。</summary>
    public bool AllowCastInsertion { get; init; }
}
