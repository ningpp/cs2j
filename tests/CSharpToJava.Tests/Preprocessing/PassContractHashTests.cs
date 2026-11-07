using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// R12: per-pass contract hash stability and the order-independent aggregate
/// used by the manifest contractHash.
/// </summary>
public sealed class PassContractHashTests
{
    [Fact]
    public void Compute_is_stable_for_the_same_contract()
    {
        Assert.Equal(PassContractHash.Compute(Contract()), PassContractHash.Compute(Contract()));
    }

    [Fact]
    public void Compute_changes_when_a_contract_field_changes()
    {
        var baseline = PassContractHash.Compute(Contract());
        var changed = PassContractHash.Compute(Contract(consumes: new[] { "NORM-OTHER" }));

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void ComputeAggregate_is_independent_of_enumeration_order()
    {
        var empty = Contract();
        var first = PassContractHash.ComputeAggregate(new[]
        {
            ("b-pass", empty),
            ("a-pass", Contract(consumes: new[] { "NORM-FIXED" })),
        });
        var second = PassContractHash.ComputeAggregate(new[]
        {
            ("a-pass", Contract(consumes: new[] { "NORM-FIXED" })),
            ("b-pass", empty),
        });

        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeAggregate_changes_when_a_contract_changes()
    {
        var baseline = PassContractHash.ComputeAggregate(new[]
        {
            ("a-pass", Contract()),
            ("b-pass", Contract()),
        });
        var changed = PassContractHash.ComputeAggregate(new[]
        {
            ("a-pass", Contract(consumes: new[] { "NORM-FIXED" })),
            ("b-pass", Contract()),
        });

        Assert.NotEqual(baseline, changed);
    }

    private static PassContract Contract(IReadOnlyList<string>? consumes = null)
        => new(
            Consumes: consumes ?? new[] { "NORM-VALUE-COPY" },
            Produces: Array.Empty<string>(),
            Preconditions: Array.Empty<string>(),
            Postconditions: new[] { "NORM-VALUE-COPY" },
            MayLeave: Array.Empty<string>(),
            Owners: Array.Empty<string>(),
            Policy: ResidualPolicy.Fail,
            Idempotent: true,
            RequiresWholeTree: false,
            CapabilityIds: new[] { "JCS.VALUE" });
}
