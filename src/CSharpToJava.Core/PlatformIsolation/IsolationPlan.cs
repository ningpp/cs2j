using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>
/// 单站点裁决（设计 §4.7）。<see cref="PlatformIsolationAction"/> 作为动作轴
/// （Reject / ProbeRedirect / ThrowStub / StripAttribute / Facade / Suppressed），
/// 与统计字段一一对应。
/// </summary>
public sealed class IsolationAction
{
    public required PlatformIsolationAction Action { get; init; }

    public required BoundarySite Site { get; init; }

    /// <summary>该动作对应的 PI 码（Reject→PI030、Probe→PI011、Throw→PI010、Strip→PI012、Suppressed→PI060）。</summary>
    public required string DiagnosticCode { get; init; }

    /// <summary>命中配置 suppress 的键（仅 Suppressed 且来自配置时非空）。</summary>
    public string? ConfigMatch { get; init; }

    /// <summary>裁决理由（消息句式用）。</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// 有序隔离动作表（设计 §4.7）。按文件分组交 StubGenerator；未裁决站点保持
/// Reject（宁错杀，R1）。统计对账不变式随 <see cref="Statistics"/> 输出。
/// </summary>
public sealed class IsolationPlan
{
    public required IReadOnlyList<IsolationAction> Actions { get; init; }

    public required PlatformIsolationStatistics Statistics { get; init; }

    public required bool HasRejections { get; init; }

    public required IReadOnlyList<string> StaleConfigEntries { get; init; }

    public IEnumerable<IsolationAction> ForFile(string filePath)
        => Actions.Where(action => string.Equals(action.Site.File, filePath, StringComparison.Ordinal));
}

/// <summary>
/// 站点分组 + D2/§2.1 默认档裁决 + 配置覆盖（只可降不可升，§4.7-5）。
/// </summary>
public static class IsolationPlanner
{
    public static IsolationPlan Build(
        BoundaryInventory inventory,
        PlatformIsolationOptions options,
        PlatformIsolationConfig? config)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(options);

        var actions = new List<IsolationAction>();
        var matchedSuppress = new HashSet<string>(StringComparer.Ordinal);

        foreach (var site in inventory.Sites)
        {
            var suppressEntry = PlatformIsolationConfigMatcher.FindSuppressEntry(config, site);
            if (suppressEntry != null)
            {
                matchedSuppress.Add(PlatformIsolationConfigMatcher.SuppressKey(suppressEntry));
            }

            var policyOverride = options.Mode == PlatformIsolationMode.Reject
                ? PlatformIsolationDefaultAction.Reject
                : ResolvePolicyOverride(config, site, options);

            var action = PlatformIsolationActionResolver.Resolve(
                site,
                policyOverride,
                suppressEntry != null);

            actions.Add(new IsolationAction
            {
                Action = action,
                Site = site,
                DiagnosticCode = DiagnosticCodeFor(action, suppressEntry != null),
                ConfigMatch = suppressEntry != null ? PlatformIsolationConfigMatcher.SuppressKey(suppressEntry) : null,
                Reason = ReasonFor(action, site),
            });
        }

        var statistics = PlatformIsolationStatistics.FromResolved(
            inventory.Sites,
            actions.Select(a => a.Action).ToList());

        var stale = new List<string>();
        if (config?.Suppress != null)
        {
            foreach (var entry in config.Suppress)
            {
                var key = PlatformIsolationConfigMatcher.SuppressKey(entry);
                if (!matchedSuppress.Contains(key))
                {
                    stale.Add(key);
                }
            }
        }

        return new IsolationPlan
        {
            Actions = actions,
            Statistics = statistics,
            HasRejections = actions.Any(a => a.Action == PlatformIsolationAction.Reject),
            StaleConfigEntries = stale,
        };
    }

    /// <summary>
    /// 配置 policy 覆盖，只可降不可升（§4.7-5）：互操作族与 UI 只允许 Reject
    /// （禁止配成静默 Stub/Facade）；平台服务类型可显式 opt-in Stub（需
    /// <see cref="PlatformIsolationOptions.EnableThrowStub"/>）或 Facade（需
    /// <see cref="PlatformIsolationOptions.EnableFacade"/>）；探针/guard 可降为 Reject。
    /// </summary>
    private static PlatformIsolationDefaultAction? ResolvePolicyOverride(
        PlatformIsolationConfig? config,
        BoundarySite site,
        PlatformIsolationOptions options)
    {
        if (config?.Policies == null || !config.Policies.TryGetValue(site.Family.ToString(), out var requested))
        {
            return null;
        }

        return site.Family switch
        {
            BoundaryFamily.UiNamespace => requested == PlatformIsolationDefaultAction.Reject ? requested : null,
            BoundaryFamily.NativeAttribute or BoundaryFamily.NativeType or BoundaryFamily.ExternMethod
                => requested == PlatformIsolationDefaultAction.Reject ? requested : null,
            BoundaryFamily.PlatformType => requested switch
            {
                PlatformIsolationDefaultAction.Stub when options.EnableThrowStub => requested,
                PlatformIsolationDefaultAction.Facade when options.EnableFacade => requested,
                PlatformIsolationDefaultAction.Reject => requested,
                _ => null,
            },
            BoundaryFamily.PlatformMember or BoundaryFamily.OsGuardAttribute
                => requested == PlatformIsolationDefaultAction.Reject ? requested : null,
            _ => null,
        };
    }

    private static string DiagnosticCodeFor(PlatformIsolationAction action, bool suppressedByConfig)
        => action switch
        {
            PlatformIsolationAction.ProbeRedirect => PlatformIsolationDiagnosticCodes.ProbeRedirected,
            PlatformIsolationAction.ThrowStub => PlatformIsolationDiagnosticCodes.ThrowStubGenerated,
            PlatformIsolationAction.StripAttribute => PlatformIsolationDiagnosticCodes.GuardAttributeStripped,
            PlatformIsolationAction.Facade => PlatformIsolationDiagnosticCodes.FacadeGenerated,
            PlatformIsolationAction.Suppressed => suppressedByConfig
                ? PlatformIsolationDiagnosticCodes.ConfigSuppressed
                : string.Empty,
            PlatformIsolationAction.Reject => PlatformIsolationDiagnosticCodes.RejectExecuted,
            _ => PlatformIsolationDiagnosticCodes.ResidualGateViolated,
        };

    private static string ReasonFor(PlatformIsolationAction action, BoundarySite site)
        => action switch
        {
            PlatformIsolationAction.ProbeRedirect => "reroute OS probe call site to Cs2jPlatformProbe",
            PlatformIsolationAction.ThrowStub => "member stub throws PlatformNotSupportedException",
            PlatformIsolationAction.StripAttribute => "strip OS guard attribute",
            PlatformIsolationAction.Facade => "facade port (M3)",
            PlatformIsolationAction.Suppressed when site.Exemption == BoundaryExemption.CompatMapping
                => "compat namespace mapping exemption",
            PlatformIsolationAction.Suppressed => "configuration suppress",
            PlatformIsolationAction.Reject => $"reject {site.Code} ({site.Identity})",
            _ => "unresolved",
        };

    /// <summary>动作 → PI 严重级（Reject/Facade 未实现为 Error；其余 Info）。</summary>
    internal static DiagnosticSeverity SeverityFor(PlatformIsolationAction action)
        => action switch
        {
            PlatformIsolationAction.Reject => DiagnosticSeverity.Error,
            PlatformIsolationAction.Facade => DiagnosticSeverity.Error,
            _ => DiagnosticSeverity.Info,
        };
}