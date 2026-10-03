namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>M4 per-host override action (design §7 overrides JSON, keyed by host full name).</summary>
public enum ExtensionHostOverrideAction
{
    /// <summary>Never select reduced sites of this host; every skip leaves an EM001 Info trace.</summary>
    Skip,

    /// <summary>
    /// Force explicitization even when a policy would leave the site sugar:
    /// RewriteExternalMetadataHosts=false and the EM-LINQ mixed-chain
    /// conservative skip. Does NOT override correctness gates (ref-mismatch,
    /// expression-tree identity rollback, non-liftable null-conditional shapes).
    /// </summary>
    Force,
}

/// <summary>
/// Parsed host-keyed overrides for the extension-methods pass (M4). Two
/// protected LINQ hosts (<c>System.Linq.Enumerable</c>/<c>System.Linq.Queryable</c>)
/// are immutable: neither force nor skip may target them — E16/C26 own their
/// reduced shape. The set of LINQ hosts is extendable only.
/// </summary>
public sealed record ExtensionHostOverrides
{
    /// <summary>The two hosts whose reduced sugar is structurally owned by E16/C26.</summary>
    public static readonly IReadOnlySet<string> ProtectedLinqHosts = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Linq.Enumerable",
        "System.Linq.Queryable",
    };

    public ExtensionHostOverrides(
        IReadOnlyDictionary<string, ExtensionHostOverrideAction>? hostActions = null,
        IReadOnlySet<string>? additionalLinqHosts = null)
        : this(Validate(hostActions), additionalLinqHosts, validateAdditional: true)
    {
    }

    private ExtensionHostOverrides(
        IReadOnlyDictionary<string, ExtensionHostOverrideAction> hostActions,
        IReadOnlySet<string>? additionalLinqHosts,
        bool validateAdditional)
    {
        HostActions = hostActions;
        AdditionalLinqHosts = additionalLinqHosts ?? new HashSet<string>(StringComparer.Ordinal);

        if (validateAdditional)
        {
            foreach (var host in AdditionalLinqHosts)
            {
                if (HostActions.ContainsKey(host))
                {
                    throw new ArgumentException(
                        $"Extension-methods overrides: host '{host}' is both a LINQ-host extension and a "
                        + "force/skip target; a host treated as LINQ-owned cannot be rewritten or skipped.",
                        nameof(additionalLinqHosts));
                }
            }
        }
    }

    /// <summary>Host full display name → action (force/skip).</summary>
    public IReadOnlyDictionary<string, ExtensionHostOverrideAction> HostActions { get; }

    /// <summary>Extra hosts whose reduced sugar is treated as LINQ-owned (extend-only).</summary>
    public IReadOnlySet<string> AdditionalLinqHosts { get; }

    public static ExtensionHostOverrides Empty { get; } =
        new(new Dictionary<string, ExtensionHostOverrideAction>(StringComparer.Ordinal), null, validateAdditional: false);

    public ExtensionHostOverrideAction? TryGetAction(string hostDisplayName)
        => HostActions.TryGetValue(hostDisplayName, out var action) ? action : null;

    private static IReadOnlyDictionary<string, ExtensionHostOverrideAction> Validate(
        IReadOnlyDictionary<string, ExtensionHostOverrideAction>? hostActions)
    {
        var result = new Dictionary<string, ExtensionHostOverrideAction>(StringComparer.Ordinal);
        if (hostActions is null)
        {
            return result;
        }

        foreach (var (host, action) in hostActions)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Extension-methods overrides: host full name must be non-empty.", nameof(hostActions));
            }

            if (ProtectedLinqHosts.Contains(host))
            {
                throw new ArgumentException(
                    $"Extension-methods overrides: protected LINQ host '{host}' is immutable (E16/C26); "
                    + "force/skip is not allowed for System.Linq.Enumerable or System.Linq.Queryable.",
                    nameof(hostActions));
            }

            result[host] = action;
        }

        return result;
    }
}
