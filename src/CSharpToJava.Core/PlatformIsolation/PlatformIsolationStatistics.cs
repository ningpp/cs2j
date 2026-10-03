namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>
/// 按 family / action 计数（设计 §4 组件表）。对账不变式：
/// <c>N = rejected + stubbed + facaded + suppressed + residual</c>；
/// 违例即 PI031（M2 闸门）——M1 报告模式 residual 恒 0，不变式应恒真。
/// </summary>
public sealed class PlatformIsolationStatistics
{
    public required int TotalSites { get; init; }

    public required IReadOnlyDictionary<BoundaryFamily, int> ByFamily { get; init; }

    public required IReadOnlyDictionary<PlatformIsolationAction, int> ByAction { get; init; }

    public int Rejected => CountOf(PlatformIsolationAction.Reject);

    /// <summary>动作档为 ProbeRedirect / ThrowStub / StripAttribute 的站点合计。</summary>
    public int Stubbed =>
        CountOf(PlatformIsolationAction.ProbeRedirect)
        + CountOf(PlatformIsolationAction.ThrowStub)
        + CountOf(PlatformIsolationAction.StripAttribute);

    public int Facaded => CountOf(PlatformIsolationAction.Facade);

    public int Suppressed => CountOf(PlatformIsolationAction.Suppressed);

    public int Residual => CountOf(PlatformIsolationAction.None);

    public bool InvariantHolds => TotalSites == Rejected + Stubbed + Facaded + Suppressed + Residual;

    public static PlatformIsolationStatistics FromSites(IReadOnlyList<BoundarySite> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);

        return FromResolved(
            sites,
            sites.Select(site => PlatformIsolationActionResolver.Resolve(site)).ToList());
    }

    /// <summary>按已裁决动作（含配置 policy/suppress 覆盖）统计。</summary>
    public static PlatformIsolationStatistics FromResolved(
        IReadOnlyList<BoundarySite> sites,
        IReadOnlyList<PlatformIsolationAction> actions)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(actions);

        var byFamily = new Dictionary<BoundaryFamily, int>();
        var byAction = new Dictionary<PlatformIsolationAction, int>();
        for (var i = 0; i < sites.Count; i++)
        {
            byFamily[sites[i].Family] = byFamily.GetValueOrDefault(sites[i].Family) + 1;
            var action = i < actions.Count ? actions[i] : PlatformIsolationAction.None;
            byAction[action] = byAction.GetValueOrDefault(action) + 1;
        }

        return new PlatformIsolationStatistics
        {
            TotalSites = sites.Count,
            ByFamily = byFamily,
            ByAction = byAction,
        };
    }

    private int CountOf(PlatformIsolationAction action)
        => ByAction.GetValueOrDefault(action);
}

/// <summary>
/// 由站点默认档 + 豁免分类裁决最终动作（设计 §2.1 / §4.7 步骤 5 的 M1 子集；
/// 配置 policy 覆盖在 Report 构建阶段应用，见 <see cref="PlatformIsolationReportBuilder"/>）。
/// </summary>
internal static class PlatformIsolationActionResolver
{
    public static PlatformIsolationAction Resolve(
        BoundarySite site,
        PlatformIsolationDefaultAction? policyOverride = null)
    {
        if (site.Exemption == BoundaryExemption.CompatMapping)
        {
            return PlatformIsolationAction.Suppressed;
        }

        var defaultAction = policyOverride ?? site.DefaultAction;
        return defaultAction switch
        {
            PlatformIsolationDefaultAction.Reject => PlatformIsolationAction.Reject,
            PlatformIsolationDefaultAction.Facade => PlatformIsolationAction.Facade,
            PlatformIsolationDefaultAction.Stub => site.Family switch
            {
                BoundaryFamily.PlatformMember => PlatformIsolationAction.ProbeRedirect,
                BoundaryFamily.OsGuardAttribute => PlatformIsolationAction.StripAttribute,
                _ => PlatformIsolationAction.ThrowStub,
            },
            _ => PlatformIsolationAction.None,
        };
    }
}