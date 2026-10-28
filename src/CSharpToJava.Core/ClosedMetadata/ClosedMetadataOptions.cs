namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Bootstrap form of the generated closed-type registration code (design §5.1,
/// decision D1: v1 ships A1 declaration-driven registration only).
/// </summary>
public enum ClosedMetadataBootstrapForm
{
    /// <summary>Register inside the declaring type's static initializer (not used in v1).</summary>
    StaticInitializer,

    /// <summary>
    /// Explicit bootstrap static method invoked before the first serialization
    /// entry (v1 form; no <c>&lt;Module&gt;</c> initializer — Java has no equivalent).
    /// </summary>
    ExplicitBootstrap,
}

/// <summary>Options for the closed-metadata (C30) preprocessing pass.</summary>
public sealed record ClosedMetadataOptions
{
    /// <summary>CM-ENUM-1: closed types appearing in field/property/event/member signatures.</summary>
    public bool RegisterDeclaredClosedTypes { get; init; } = true;

    /// <summary>CM-ENUM-2: explicit <c>typeof</c>/<c>default</c>/cast/<c>new</c> closed-type sites.</summary>
    public bool RegisterExplicitTypeOfSites { get; init; } = true;

    /// <summary>CM-ENUM-3: type arguments at serializer call sites (JsonSerializer family).</summary>
    public bool RegisterSerializerCallSites { get; init; } = true;

    /// <summary>CM-ENUM-4: closed types from override/interface implementation family signatures.</summary>
    public bool RegisterSignatureSites { get; init; } = true;

    /// <summary>Bootstrap form of the generated registration file (v1 = explicit bootstrap, D1/D6).</summary>
    public ClosedMetadataBootstrapForm BootstrapForm { get; init; } =
        ClosedMetadataBootstrapForm.ExplicitBootstrap;

    /// <summary>Upgrade residual-gate and downgradeable diagnostics to errors.</summary>
    public bool Strict { get; init; }

    /// <summary>
    /// Explicit consumer surfaces served by the registration (decision D8).
    /// Empty = the default surface set (STJ metadata chain + compat closed-type
    /// pure-function callers); entries are symbol-full-name prefixes.
    /// </summary>
    public IReadOnlyList<string> ConsumerSurfaces { get; init; } = Array.Empty<string>();

    /// <summary>Optional per-type/member skip+force overrides file path (design §6.3).</summary>
    public string? OverridesPath { get; init; }

    /// <summary>
    /// Ids of every pass enabled in the running chain. The CLI adapter reads this
    /// to enforce the generic-deepen hard chain dependency (CS2J1205) — same
    /// plan-aware-options pattern as numeric-boundary.
    /// </summary>
    public IReadOnlySet<string>? EnabledPassIds { get; init; }

    /// <summary>
    /// Optional predicate marking source files that belong to injected cs2j
    /// support libraries (symbol-source-only projects that never convert to
    /// Java). Closed constructions declared in such files follow the framework
    /// branch of the D6 ownership rule — registration ownership falls back to
    /// the discovery-site project — because the support projects cannot
    /// reference user projects and a <c>typeof(...)</c> registration naming
    /// user types cannot compile there.
    /// </summary>
    public Func<string, bool>? IsInfrastructureSource { get; init; }

    /// <summary>
    /// Optional resolver mapping a source file path to the identity of the project
    /// that compiles it (e.g. the absolute project-file path). The D6 ownership
    /// rule uses it to detect a closed type whose generic arguments are declared
    /// in projects the declaring project cannot name (GraphLayout: the Set&lt;Edge&gt;
    /// construction is declared in MSAGL while Edge lives in Drawing, which MSAGL
    /// does not reference). Such records fall back to the discovery-site project.
    /// </summary>
    public Func<string, string?>? ProjectIdentityResolver { get; init; }

    /// <summary>
    /// Whether project <paramref name="from"/> can name types declared in project
    /// <paramref name="to"/> (direct or transitive <c>ProjectReference</c>). Paired
    /// with <see cref="ProjectIdentityResolver"/>; when either is unset the D6
    /// ownership rule keeps the legacy declaration-project behavior.
    /// </summary>
    public Func<string, string, bool>? IsProjectVisible { get; init; }
}
