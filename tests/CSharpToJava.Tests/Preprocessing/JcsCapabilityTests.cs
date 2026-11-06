using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// VS-0 step 2 (upgrade plan 附录 I.1/I.2): the JCS capability model types
/// and the <see cref="PassContract"/> incremental fields
/// (CapabilityIds/Phase/FinalizerFor). The existing 9-field constructions
/// must keep compiling unchanged (defaults), and the controlled
/// <see cref="JcsCapability"/> construction surface must keep Guaranteed
/// scanner-only (invariant I11).
/// </summary>
public sealed class JcsCapabilityTests
{
    // ---- JcsCapabilityIds format/parse round trip ----

    [Theory]
    [InlineData(JcsCapabilityId.Workspace, "JCS.WORKSPACE")]
    [InlineData(JcsCapabilityId.Symbol, "JCS.SYMBOL")]
    [InlineData(JcsCapabilityId.Call, "JCS.CALL")]
    [InlineData(JcsCapabilityId.Eval, "JCS.EVAL")]
    [InlineData(JcsCapabilityId.Value, "JCS.VALUE")]
    [InlineData(JcsCapabilityId.Generic, "JCS.GENERIC")]
    [InlineData(JcsCapabilityId.Conversion, "JCS.CONVERSION")]
    [InlineData(JcsCapabilityId.Closure, "JCS.CLOSURE")]
    public void Format_produces_the_canonical_capability_string(
        JcsCapabilityId id, string expected)
    {
        Assert.Equal(expected, JcsCapabilityIds.Format(id));
    }

    [Theory]
    [InlineData("JCS.VALUE")]
    [InlineData("JCS.value")]
    [InlineData("JCS.Value")]
    public void TryParse_accepts_canonical_and_case_variants(string text)
    {
        Assert.True(JcsCapabilityIds.TryParse(text, out var id));
        Assert.Equal(JcsCapabilityId.Value, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("JCS.")]
    [InlineData("VALUE")]
    [InlineData("value")]
    [InlineData("JCS.NOT_A_FAMILY")]
    [InlineData("jcs.value")]
    [InlineData(" JCS.VALUE")]
    public void TryParse_rejects_malformed_or_unknown_values(string? text)
    {
        Assert.False(JcsCapabilityIds.TryParse(text, out _));
    }

    // ---- JcsCapability controlled construction ----

    [Fact]
    public void Partial_factory_keeps_residuals_and_no_evidence_hash()
    {
        var capability = JcsCapability.Partial(
            JcsCapabilityId.Conversion,
            new[] { new JcsCapabilityResidual("A.cs", "collection-shape-normalize", "open-world") },
            constructs: new[] { "span-to-enumerable" },
            finalizers: Array.Empty<string>());

        Assert.Equal(JcsCapabilityId.Conversion, capability.Id);
        Assert.Equal(JcsCapabilityStatus.Partial, capability.Status);
        var residual = Assert.Single(capability.Residuals);
        Assert.Equal("A.cs", residual.File);
        Assert.Equal("collection-shape-normalize", residual.Owner);
        Assert.Equal("open-world", residual.Reason);
        Assert.Null(capability.EvidenceHash);
    }

    [Fact]
    public void Unsupported_factory_carries_the_reason_as_a_residual()
    {
        var capability = JcsCapability.Unsupported(
            JcsCapabilityId.Generic, "runtime-bound type parameter");

        Assert.Equal(JcsCapabilityStatus.Unsupported, capability.Status);
        var residual = Assert.Single(capability.Residuals);
        Assert.Equal("runtime-bound type parameter", residual.Reason);
        Assert.Empty(capability.Constructs);
        Assert.Empty(capability.Finalizers);
    }

    [Fact]
    public void Guaranteed_is_scanner_only_and_stamps_the_evidence_hash()
    {
        // Internal factory: only the Core-side semantic closure scanner may
        // derive a Guaranteed claim (invariant I11). This test pins the
        // shape via InternalsVisibleTo.
        var capability = JcsCapability.GuaranteedForScanner(
            JcsCapabilityId.Value,
            constructs: new[] { "struct-value-copy-site" },
            finalizers: new[] { "struct-value-semantics/finalize" },
            evidenceHash: "sha256:abc");

        Assert.Equal(JcsCapabilityStatus.Guaranteed, capability.Status);
        Assert.Empty(capability.Residuals);
        Assert.Equal("sha256:abc", capability.EvidenceHash);
        Assert.Equal(new[] { "struct-value-copy-site" }, capability.Constructs);
    }

    // ---- PassContract incremental fields (backward compatible) ----

    [Fact]
    public void PassContract_existing_nine_field_construction_keeps_defaults()
    {
        var contract = new PassContract(
            Consumes: new[] { "NORM-FIXED" },
            Produces: Array.Empty<string>(),
            Preconditions: Array.Empty<string>(),
            Postconditions: new[] { "NORM-FIXED" },
            MayLeave: Array.Empty<string>(),
            Owners: Array.Empty<string>(),
            Policy: ResidualPolicy.Fail,
            Idempotent: true,
            RequiresWholeTree: false);

        Assert.Null(contract.CapabilityIds);
        Assert.Empty(contract.EffectiveCapabilityIds);
        Assert.Equal(PassPhase.Rewrite, contract.Phase);
        Assert.Null(contract.FinalizerFor);
    }

    [Fact]
    public void PassContract_incremental_fields_round_trip()
    {
        var contract = new PassContract(
            Consumes: new[] { "NORM-VALUE-COPY" },
            Produces: Array.Empty<string>(),
            Preconditions: Array.Empty<string>(),
            Postconditions: new[] { "NORM-VALUE-COPY" },
            MayLeave: Array.Empty<string>(),
            Owners: Array.Empty<string>(),
            Policy: ResidualPolicy.Fail,
            Idempotent: true,
            RequiresWholeTree: false,
            CapabilityIds: new[] { "JCS.VALUE" },
            Phase: PassPhase.Finalize,
            FinalizerFor: "JCS.VALUE");

        Assert.Equal(new[] { "JCS.VALUE" }, contract.EffectiveCapabilityIds);
        Assert.Equal(PassPhase.Finalize, contract.Phase);
        Assert.Equal("JCS.VALUE", contract.FinalizerFor);
    }
}
