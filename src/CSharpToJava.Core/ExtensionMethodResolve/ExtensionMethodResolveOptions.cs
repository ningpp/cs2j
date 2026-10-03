namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Policy for extension call sites found inside an
/// <c>Expression&lt;...&gt;</c> lambda (EM-EXPRTREE, design §2).
/// </summary>
public enum ExpressionTreeRewritePolicy
{
    /// <summary>Rewrite and assert the rebound node keeps the same MethodInfo/argument shape.</summary>
    RewriteWithIdentityAssert,

    /// <summary>Report the site (Info) and leave the sugar untouched.</summary>
    ReportOnly,
}

/// <summary>Options for the extension-method-resolve (C19) preprocessing pass.</summary>
public sealed record ExtensionMethodResolveOptions
{
    /// <summary>Upgrade Warning-level diagnostics (1201-1207/1210/1220/1203) to Error.</summary>
    public bool Strict { get; init; }

    /// <summary>Emit Info-level diagnostics (EM001 per-site detail is verbose-only anyway).</summary>
    public bool ReportInfo { get; init; } = true;

    /// <summary>Run the post-rewrite residual scan (strict turns findings into errors).</summary>
    public bool RunResidualScan { get; init; } = true;

    /// <summary>
    /// Rewrite reduced calls whose host comes from metadata (BCL/NuGet) instead
    /// of from the merged compilation (EM-EXTERNAL). Even when rewritten, hosts
    /// without a Java mapping are reported via CS2J1203 (M3).
    /// </summary>
    public bool RewriteExternalMetadataHosts { get; init; } = true;

    /// <summary>
    /// LINQ host full names whose reduced sugar is NEVER touched (EM-LINQ):
    /// E16/C26 structurally depend on the reduced member-access shape.
    /// Extendable only (overrides may add hosts, never remove these two;
    /// design §10 risk 1).
    /// </summary>
    public IReadOnlySet<string> LinqHostSet { get; init; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Linq.Enumerable",
        "System.Linq.Queryable",
    };

    /// <summary>Expression-tree site policy (default: rewrite + identity assert).</summary>
    public ExpressionTreeRewritePolicy ExpressionTreePolicy { get; init; }
        = ExpressionTreeRewritePolicy.RewriteWithIdentityAssert;

    /// <summary>
    /// Path to an overrides file keyed by host full name (force/skip per host).
    /// Empty in M0; every skip performed via overrides leaves an Info trace.
    /// </summary>
    public string? OverridesPath { get; init; }
}
