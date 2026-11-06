using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Core.Preprocessing;

/// <summary>
/// The pipeline role a pass plays for the JCS capability model (upgrade plan
/// 附录 I.2): <see cref="Rewrite"/> passes produce/eliminate constructs in
/// chain order; <see cref="Finalize"/> passes are bounded finalizers that run
/// ONCE per round after all Produces owners of their capability (see
/// <see cref="PassContract.FinalizerFor"/>).
/// </summary>
public enum PassPhase
{
    /// <summary>Default: an ordinary chain-order rewriting pass.</summary>
    Rewrite,

    /// <summary>
    /// A bounded finalizer: runs once after the passes it finalizes; must
    /// declare <see cref="PassContract.FinalizerFor"/> and generate no
    /// unowned constructs.
    /// </summary>
    Finalize,
}

/// <summary>
/// Machine-readable contract for one preprocess pass. Construct ids are the
/// stable <see cref="PassConstructIds"/> values, never diagnostic codes.
/// </summary>
/// <param name="Consumes">Constructs this pass eliminates.</param>
/// <param name="Produces">Constructs this pass introduces for later consumers.</param>
/// <param name="Preconditions">Constructs that must already be absent before the pass runs.</param>
/// <param name="Postconditions">Constructs that must be absent after the pass runs.</param>
/// <param name="MayLeave">Constructs this pass may leave behind for an owner to take over.</param>
/// <param name="Owners">Pass ids that take over the <paramref name="MayLeave"/> constructs.</param>
/// <param name="Policy">Residual disposition shared with the normalization audit.</param>
/// <param name="Idempotent">Whether rerunning the pass over its own output is a fixed point.</param>
/// <param name="RequiresWholeTree">Whether the pass needs a whole-tree compilation.</param>
/// <param name="CapabilityIds">
/// JCS capabilities this pass contributes to, as <c>"JCS.&lt;ID&gt;"</c> strings
/// (e.g. <c>"JCS.VALUE"</c>); null/empty for passes with no capability claim.
/// Declaring a capability here does NOT grant it — only the final-tree
/// semantic closure scan can mark a capability Guaranteed (invariant I11).
/// </param>
/// <param name="Phase">
/// Pipeline role for the JCS capability model; defaults to
/// <see cref="PassPhase.Rewrite"/>. A <see cref="PassPhase.Finalize"/> pass
/// must also set <paramref name="FinalizerFor"/>.
/// </param>
/// <param name="FinalizerFor">
/// For <see cref="PassPhase.Finalize"/> passes only: the capability
/// (a <c>"JCS.&lt;ID&gt;"</c> string) or construct family this pass is the
/// bounded finalizer of. The pass must run after every Produces owner of
/// that capability (checked by <c>PassContractConsistencyTests</c>).
/// </param>
public sealed record PassContract(
    IReadOnlyList<string> Consumes,
    IReadOnlyList<string> Produces,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> Postconditions,
    IReadOnlyList<string> MayLeave,
    IReadOnlyList<string> Owners,
    ResidualPolicy Policy,
    bool Idempotent,
    bool RequiresWholeTree,
    IReadOnlyList<string>? CapabilityIds = null,
    PassPhase Phase = PassPhase.Rewrite,
    string? FinalizerFor = null)
{
    /// <summary><paramref name="CapabilityIds"/> normalized to a non-null sequence.</summary>
    public IReadOnlyList<string> EffectiveCapabilityIds
        => CapabilityIds ?? Array.Empty<string>();
}
