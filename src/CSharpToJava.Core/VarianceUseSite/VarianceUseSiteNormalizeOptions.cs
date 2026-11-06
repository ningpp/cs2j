namespace CSharpToJava.Core.VarianceUseSite;

/// <summary>
/// Options for the <c>variance-use-site-normalize</c> (VR) pass. The pass reuses
/// the C33 <c>VarianceAuditAnalysis</c>/<c>UseSiteSafetyClassifier</c> inventory
/// (never a second variance analysis) and rewrites only sites the shared safety
/// classifier already accepted.
/// </summary>
public sealed class VarianceUseSiteNormalizeOptions
{
    /// <summary>
    /// Apply the provable rewrites (<c>(T)(object)expr</c>). When false the pass
    /// still classifies and reports every site but leaves the tree untouched.
    /// </summary>
    public bool Rewrite { get; init; } = true;

    /// <summary>
    /// Escalate <c>DeclaredUnsupported</c> sites to Error (CI gate). Default false
    /// keeps them Warnings so the pass can run over a real corpus without
    /// aborting the chain; the machine-readable report always keeps every site.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>
    /// D9 mapped-mutability gate: a producer rewrite is refused when the Java
    /// mapping of the target type exposes mutating members the C# contract does
    /// not have. Default on (same seed table as C33).
    /// </summary>
    public bool CheckMappedMutability { get; init; } = true;
}
