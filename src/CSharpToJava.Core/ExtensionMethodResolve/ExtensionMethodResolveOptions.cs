using Microsoft.CodeAnalysis;

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

    /// <summary>
    /// Emit the EM001 per-bound-site triage line (host full name,
    /// classification, mapping grade, generic/ref/expression-tree flags).
    /// Default off; the CLI adapter turns it on in verbose runs.
    /// </summary>
    public bool ReportSiteDetail { get; init; }

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
    /// Extendable only (M4 enforcement in
    /// <see cref="ExtensionMethodResolvePreprocessor.Analyze"/>: a set missing
    /// either protected host throws <see cref="InvalidOperationException"/>;
    /// additional hosts may be supplied via <see cref="HostOverrides"/>;
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
    /// M4: the CLI adapter loads this into <see cref="HostOverrides"/>; Core
    /// callers may set <see cref="HostOverrides"/> directly. Every skip
    /// performed via overrides leaves an EM001 Info trace.
    /// </summary>
    public string? OverridesPath { get; init; }

    /// <summary>
    /// M4: parsed host-keyed force/skip overrides plus extend-only LINQ host
    /// additions. Null (default) means no overrides.
    /// </summary>
    public ExtensionHostOverrides? HostOverrides { get; init; }

    /// <summary>
    /// M3 (EM-EXTERNAL, design §4.2-3): grades an external (metadata-only)
    /// extension host for Java-landing availability. The CLI adapter supplies
    /// a TypeMappings-backed implementation; Core never loads TypeMappings.
    /// Null means "grading unavailable" — no CS2J1203 is emitted and the site
    /// is rewritten silently (keeps the M1/M2 behavior for Core-only tests).
    /// </summary>
    public Func<INamedTypeSymbol, ExternalHostMappingKind>? ExternalHostMapper { get; init; }

    /// <summary>
    /// M3: decides whether a host whose symbol lives in source inside the
    /// merged compilation is an injected support library
    /// (Cs2jPrelude/Cs2jRefOutIn/Cs2jUnsignedMath/Cs2jArray) rather than user
    /// code. The CLI adapter builds this from the reserved support-library
    /// directory/project-name contract. Null classifies every source host as
    /// <see cref="ReceiverClassification.InCompilationUser"/>.
    /// </summary>
    public Func<INamedTypeSymbol, bool>? IsSupportLibraryHost { get; init; }

    /// <summary>
    /// M3 (CS2J1290, design §4.2-5): true in single-file/loose-directory mode
    /// where no project graph (and therefore no restore/NuGet reference set) is
    /// available. Extension-like calls that fail to bind are conservatively
    /// left unchanged and reported as CS2J1290 Warning instead of CS2J1202.
    /// </summary>
    public bool SingleFileIncompleteReferences { get; init; }
}
