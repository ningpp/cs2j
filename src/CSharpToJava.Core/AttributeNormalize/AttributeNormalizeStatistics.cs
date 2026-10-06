namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>
/// attribute-normalize pass 统计（design §4.1）。
/// </summary>
public sealed class AttributeNormalizeStatistics
{
    // ----- 通用文件计数 -----
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int FilesDirtyNoRewrite { get; set; }
    public int ResidualCount { get; set; }

    // ----- Caller plan -----
    public int CallerPlansDetected { get; set; }
    public int CallerPlansApplied { get; set; }
    public int CallerPlansSkipped { get; set; }

    /// <summary>已填参站点数，按四族分计数。</summary>
    public Dictionary<CallerInfoKind, int> CallerSitesFilled { get; } = new();

    public int CallerAttributesRemoved { get; set; }

    // ----- inventory -----
    /// <summary>盘点 attribute 总数，按 family 分桶。</summary>
    public Dictionary<AttributeFamily, int> AttributesInventoried { get; } = new();

    public int BridgeGaps { get; set; }
    public int UnknownAttributes { get; set; }

    /// <summary>
    /// 属性类型在预处理编译中不可绑定、且未命中唯一短名别名的使用点数
    /// （只刷 AN004 Info，不参与 CS2J1320 triage）。
    /// </summary>
    public int UnresolvedAttributes { get; set; }

    public int PlatformAttributesInventoried { get; set; }

    // ----- 回退 -----
    public int PlansRolledBack { get; set; }

    /// <summary>跳过 plan 数，按诊断码分桶。</summary>
    public Dictionary<string, int> SkippedByReason { get; } = new(StringComparer.Ordinal);

    public void RecordFilledSite(CallerInfoKind kind)
    {
        CallerSitesFilled.TryGetValue(kind, out var cnt);
        CallerSitesFilled[kind] = cnt + 1;
    }
}
