namespace CSharpToJava.Core.Preprocessing.Jcs;

/// <summary>
/// Parsing/formatting for the <c>"JCS.&lt;ID&gt;"</c> capability strings used
/// by <c>PassContract.CapabilityIds</c>/<c>PassContract.FinalizerFor</c> and
/// the manifest, so every consumer shares one spelling.
/// </summary>
public static class JcsCapabilityIds
{
    public const string Prefix = "JCS.";

    /// <summary>Canonical string form, e.g. <c>"JCS.VALUE"</c>.</summary>
    public static string Format(JcsCapabilityId id)
        => Prefix + id.ToString().ToUpperInvariant();

    /// <summary>
    /// Parses a <c>"JCS.&lt;ID&gt;"</c> string (case-insensitive on the id
    /// segment, ordinal on the prefix). Anything else — including a bare
    /// <c>"VALUE"</c> — is rejected so typos surface in contract consistency
    /// tests instead of silently granting nothing.
    /// </summary>
    public static bool TryParse(string? text, out JcsCapabilityId id)
    {
        id = default;
        if (string.IsNullOrEmpty(text)
            || !text.StartsWith(Prefix, StringComparison.Ordinal)
            || text.Length <= Prefix.Length)
        {
            return false;
        }

        return Enum.TryParse(text[Prefix.Length..], ignoreCase: true, out id);
    }
}

/// <summary>
/// JCS-1 capability families (upgrade plan §0.1.1): the final-tree
/// postconditions the preprocessed C# tree can guarantee. Each capability is
/// granted only by a final-tree scan (never inferred from "a pass ran"), so
/// the ids here are referenced by <c>PassContract.CapabilityIds</c> and by the
/// (future) manifest emitter — they carry no rendering instructions.
/// </summary>
public enum JcsCapabilityId
{
    /// <summary>Input evaluated per project × TFM × configuration; no degraded workspace.</summary>
    Workspace,

    /// <summary>Healthy compilation; ABI changes and generated symbols registered.</summary>
    Symbol,

    /// <summary>Call shapes fixed per C# overload resolution; no callee guessing.</summary>
    Call,

    /// <summary>Conditional/short-circuit/byref evaluation order explicit; no dangling pre/post statements.</summary>
    Eval,

    /// <summary>Struct value-copy slots either carry a controlled copy or a machine-readable no-copy reason.</summary>
    Value,

    /// <summary>Type tokens, closed-type metadata, constraint carry and static generic state per R0~R10.</summary>
    Generic,

    /// <summary>Accepted collection-shape conversions exist explicitly in the source tree.</summary>
    Conversion,

    /// <summary>Late-generated constructs are closed over by a final-tree semantic scan.</summary>
    Closure,
}

/// <summary>
/// Three-valued capability status (upgrade plan §0.1.1): only
/// <see cref="Guaranteed"/> with residual=0 can authorize retiring a Java
/// compensation branch; <see cref="Partial"/> must keep the downstream
/// fallback and list the leftovers; <see cref="Unsupported"/> fails
/// java-ready.
/// </summary>
public enum JcsCapabilityStatus
{
    Guaranteed,
    Partial,
    Unsupported,
}

/// <summary>One leftover construct keeping a capability away from Guaranteed.</summary>
public sealed record JcsCapabilityResidual(
    string File,
    string Owner,
    string Reason);

/// <summary>
/// One capability claim about the FINAL preprocessed tree (upgrade plan
/// §0.1.1/附录 I.1). Construction is controlled on purpose:
/// <see cref="Partial"/> and <see cref="Unsupported"/> are the only public
/// factories, while <see cref="Guaranteed"/> is reserved for the semantic
/// closure scanner (Core-internal) — adapters must never hand-write a
/// Guaranteed claim (invariant I11: capability is derived from final-tree
/// evidence, not from "a pass executed").
/// </summary>
public sealed record JcsCapability
{
    public JcsCapabilityId Id { get; }
    public JcsCapabilityStatus Status { get; }
    public IReadOnlyList<string> Constructs { get; }
    public IReadOnlyList<string> Finalizers { get; }
    public IReadOnlyList<JcsCapabilityResidual> Residuals { get; }
    public string? EvidenceHash { get; }

    private JcsCapability(
        JcsCapabilityId id,
        JcsCapabilityStatus status,
        IReadOnlyList<string> constructs,
        IReadOnlyList<string> finalizers,
        IReadOnlyList<JcsCapabilityResidual> residuals,
        string? evidenceHash)
    {
        Id = id;
        Status = status;
        Constructs = constructs;
        Finalizers = finalizers;
        Residuals = residuals;
        EvidenceHash = evidenceHash;
    }

    /// <summary>
    /// A partially guaranteed capability: the listed residuals keep the
    /// downstream Java fallback alive (each with file/owner/reason).
    /// </summary>
    public static JcsCapability Partial(
        JcsCapabilityId id,
        IReadOnlyList<JcsCapabilityResidual> residuals,
        IReadOnlyList<string> constructs,
        IReadOnlyList<string> finalizers)
        => new(id, JcsCapabilityStatus.Partial, constructs, finalizers, residuals, null);

    /// <summary>
    /// A capability the current chain explicitly does not provide; the reason
    /// must be machine-consumable for java-ready gating.
    /// </summary>
    public static JcsCapability Unsupported(JcsCapabilityId id, string reason)
        => new(
            id,
            JcsCapabilityStatus.Unsupported,
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[] { new JcsCapabilityResidual(string.Empty, string.Empty, reason) },
            null);

    /// <summary>
    /// Scanner-only factory for a Guaranteed claim: the semantic closure
    /// scanner derives it from final-tree evidence (target constructs
    /// residual=0, degraded=0, declared finalizers executed) and stamps the
    /// evidence hash. Kept Core-internal so no adapter can hand-write it.
    /// </summary>
    internal static JcsCapability GuaranteedForScanner(
        JcsCapabilityId id,
        IReadOnlyList<string> constructs,
        IReadOnlyList<string> finalizers,
        string evidenceHash)
        => new(
            id,
            JcsCapabilityStatus.Guaranteed,
            constructs,
            finalizers,
            Array.Empty<JcsCapabilityResidual>(),
            evidenceHash);
}
