using CSharpToJava.Core.Normalization;

namespace CSharpToJava.Core.Preprocessing;

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
public sealed record PassContract(
    IReadOnlyList<string> Consumes,
    IReadOnlyList<string> Produces,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> Postconditions,
    IReadOnlyList<string> MayLeave,
    IReadOnlyList<string> Owners,
    ResidualPolicy Policy,
    bool Idempotent,
    bool RequiresWholeTree);
