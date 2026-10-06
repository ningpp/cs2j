using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>attribute 语义族（design §4.1，M1 枚举完整）。</summary>
public enum AttributeFamily
{
    CallerInfo,
    JsonSystemTextJson,
    JsonNewtonsoft,
    DataContract,
    XmlSerialization,
    Debugger,
    Platform,
    Interop,
    Tests,
    Flags,
    Compiler,
    SourceGenerator,
    Custom,
    Other,
}

/// <summary>桥接状态（design §4.1）。</summary>
public enum BridgeStatus
{
    Bridged,
    InventoriedPassthrough,
    BridgeGap,
    GovernedElsewhere,
    Unknown,
    Ignored,

    /// <summary>
    /// 属性类型在预处理编译中无法绑定（典型：来自 NuGet 包而预处理编译只带
    /// TPA + ProjectReference 引用），且语法短名无法在分类表中唯一别名。
    /// 与 <see cref="Unknown"/> 的区别：Unknown 是「工具看得见符号、用户必须
    /// triage」；Unresolved 是「编译输入不完整、无人能 triage」，只记 AN004
    /// Info（verbose），不刷 CS2J1320、不参与 strict 升级。
    /// </summary>
    Unresolved,
}

/// <summary>
/// 单个 attribute 使用点（design §4.1）。
/// </summary>
public sealed record AttributeOccurrence(
    string AttributeMetadataName,
    AttributeFamily Family,
    BridgeStatus Status,
    ISymbol? TargetSymbol,
    Location Location,
    IReadOnlyList<string> MaterializedArguments,
    IReadOnlyList<string> UnreadArguments,
    string? OwnerPass,
    IReadOnlyList<string> GoverningDiagnosticCodes)
{
    /// <summary>确定性排序键：文件路径 + span。</summary>
    public string OrderKey
        => $"{Location.SourceTree?.FilePath ?? string.Empty}#{Location.SourceSpan.Start}";
}

/// <summary>
/// 全树 attribute inventory 报告（design §4.1）。
/// 出现点按「文件 + span」确定性排序；描述的是 C10 输入树，不随回退变化。
/// </summary>
public sealed class AttributeInventoryReport
{
    public required IReadOnlyList<AttributeOccurrence> Occurrences { get; init; }

    public IEnumerable<AttributeOccurrence> ByFamily(AttributeFamily family)
        => Occurrences.Where(o => o.Family == family);

    /// <summary>
    /// 按主管辖 pass 过滤出现点（design §11 C06/C42 行：只读消费面，
    /// 供未来 C06/C42 稳定查询，不做任何归一判定）。
    /// </summary>
    public IEnumerable<AttributeOccurrence> ByOwnerPass(string ownerPass)
        => Occurrences.Where(o => string.Equals(
            o.OwnerPass, ownerPass, StringComparison.Ordinal));

    public int CountByFamily(AttributeFamily family)
    {
        var cnt = 0;
        foreach (var o in Occurrences)
            if (o.Family == family) cnt++;
        return cnt;
    }

    public int CountByStatus(BridgeStatus status)
    {
        var cnt = 0;
        foreach (var o in Occurrences)
            if (o.Status == status) cnt++;
        return cnt;
    }

    public static AttributeInventoryReport Empty { get; }
        = new() { Occurrences = Array.Empty<AttributeOccurrence>() };
}
