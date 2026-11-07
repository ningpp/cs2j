using System.Security.Cryptography;
using System.Text;

namespace CSharpToJava.Core.Preprocessing.Jcs;

/// <summary>
/// R12: stable SHA-256 over a <see cref="PassContract"/> and order-independent
/// aggregate over a pass plan. List members keep their declaration order;
/// field names are part of the canonical text so adding a field changes the
/// hash. The contract itself is never mutated.
/// </summary>
public static class PassContractHash
{
    public static string Compute(PassContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var canonical = string.Join(
            '|',
            "consumes=" + Join(contract.Consumes),
            "produces=" + Join(contract.Produces),
            "preconditions=" + Join(contract.Preconditions),
            "postconditions=" + Join(contract.Postconditions),
            "mayLeave=" + Join(contract.MayLeave),
            "owners=" + Join(contract.Owners),
            "policy=" + contract.Policy,
            "idempotent=" + Bool(contract.Idempotent),
            "requiresWholeTree=" + Bool(contract.RequiresWholeTree),
            "capabilityIds=" + Join(contract.EffectiveCapabilityIds),
            "phase=" + contract.Phase,
            "finalizerFor=" + (contract.FinalizerFor ?? string.Empty));

        return Hash(canonical);
    }

    /// <summary>Hash used for a pass whose descriptor declares no contract.</summary>
    public static string ComputeMissingContract()
        => Hash("no-contract");

    /// <summary>
    /// Aggregate hash of the pass plan: lines are <c>id:hash</c>, sorted by id
    /// ordinal so enumeration order never affects the manifest.
    /// </summary>
    public static string ComputeAggregate(IEnumerable<(string Id, PassContract Contract)> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);

        return ComputeAggregateHashes(
            passes.Select(pass => (pass.Id, Compute(pass.Contract))));
    }

    /// <summary>
    /// Aggregate over already-computed pass hashes, so passes without a
    /// contract can contribute <see cref="ComputeMissingContract"/>.
    /// </summary>
    public static string ComputeAggregateHashes(IEnumerable<(string Id, string ContractHash)> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);

        var lines = passes
            .OrderBy(pass => pass.Id, StringComparer.Ordinal)
            .Select(pass => pass.Id + ":" + pass.ContractHash);

        return Hash(string.Join('\n', lines));
    }

    private static string Join(IReadOnlyList<string>? values)
        => values is null ? string.Empty : string.Join(',', values);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexStringLower(bytes);
    }
}
