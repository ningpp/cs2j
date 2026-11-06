using System.Reflection;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// M5.3-a: the NORM construct ids, the residual registry and the pass contracts
/// share one source of truth. VS-0 step 2 (upgrade plan 附录 I.2) adds the
/// JCS capability/phase/finalizer derived assertions.
/// </summary>
public sealed class PassContractConsistencyTests
{
    private static string[] DeclaredConstructIds()
        => typeof(PassConstructIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    [Fact]
    public void PassConstructIds_are_unique_strings()
    {
        var ids = DeclaredConstructIds();

        Assert.Equal(10, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith("NORM-", id, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_residual_descriptor_code_is_a_declared_construct_id()
    {
        var ids = DeclaredConstructIds();

        foreach (var descriptor in ResidualRegistry.CreateDefault().Descriptors)
        {
            Assert.Contains(descriptor.Code, ids);
        }
    }

    [Fact]
    public void Every_residual_descriptor_owner_resolves_to_a_registered_pass()
    {
        var registry = PassCatalog.BuildRegistry();

        foreach (var descriptor in ResidualRegistry.CreateDefault().Descriptors)
        {
            Assert.True(
                registry.TryGet(descriptor.OwnerPassId, out _),
                $"residual '{descriptor.Code}' names unknown owner pass '{descriptor.OwnerPassId}'.");
        }
    }

    [Fact]
    public void Owner_pass_contracts_mention_their_residual_code()
    {
        var registry = PassCatalog.BuildRegistry();

        foreach (var descriptor in ResidualRegistry.CreateDefault().Descriptors)
        {
            var owner = registry.Require(descriptor.OwnerPassId);
            if (owner.Contract is null)
                continue;

            var declared = owner.Contract.Consumes
                .Concat(owner.Contract.Postconditions)
                .Concat(owner.Contract.MayLeave);

            Assert.Contains(descriptor.Code, declared);
        }
    }

    [Fact]
    public void Normalization_audit_stays_last_and_default_off()
    {
        var registry = PassCatalog.BuildRegistry();

        Assert.Equal(
            PassIds.NormalizationAudit,
            registry.Ordered.Last(pass => pass.Status == PassStatus.Implemented).Id);
        Assert.False(registry.Require(PassIds.NormalizationAudit).DefaultEnabled);
    }

    // ---- VS-0 step 2 (upgrade plan 附录 I.2): JCS capability metadata ----

    [Fact]
    public void Contract_capability_ids_are_wellformed_jcs_families()
    {
        foreach (var pass in PassCatalog.BuildRegistry().Ordered)
        {
            if (pass.Contract is null)
                continue;

            foreach (var capability in pass.Contract.EffectiveCapabilityIds)
            {
                Assert.True(
                    JcsCapabilityIds.TryParse(capability, out _),
                    $"pass '{pass.Id}' declares malformed capability id " +
                    $"'{capability}' (expected \"JCS.<ID>\" with ID in " +
                    $"{nameof(JcsCapabilityId)}).");
            }
        }
    }

    [Fact]
    public void Finalize_phase_requires_a_finalizer_for_target()
    {
        foreach (var pass in PassCatalog.BuildRegistry().Ordered)
        {
            if (pass.Contract?.Phase != PassPhase.Finalize)
                continue;

            Assert.False(
                string.IsNullOrEmpty(pass.Contract.FinalizerFor),
                $"pass '{pass.Id}' declares Phase=Finalize without FinalizerFor.");
            Assert.True(
                JcsCapabilityIds.TryParse(pass.Contract.FinalizerFor, out _),
                $"pass '{pass.Id}' FinalizerFor '{pass.Contract.FinalizerFor}' " +
                "is not a wellformed \"JCS.<ID>\" capability id.");
        }
    }

    [Fact]
    public void Finalizers_run_after_every_rewrite_owner_of_their_capability()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered;
        var positionById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
            positionById[ordered[i].Id] = i;

        foreach (var finalizer in ordered)
        {
            var contract = finalizer.Contract;
            if (contract?.Phase != PassPhase.Finalize)
                continue;

            foreach (var producer in ordered)
            {
                var producerContract = producer.Contract;
                if (producerContract is null
                    || producerContract.Phase != PassPhase.Rewrite
                    || !producerContract.EffectiveCapabilityIds.Contains(
                        contract.FinalizerFor!, StringComparer.Ordinal))
                {
                    continue;
                }

                Assert.True(
                    positionById[producer.Id] < positionById[finalizer.Id],
                    $"finalizer '{finalizer.Id}' must run after '{producer.Id}' " +
                    $"(both declare capability '{contract.FinalizerFor}').");
            }
        }
    }

    [Fact]
    public void No_pass_claims_guaranteed_status_outside_the_scanner()
    {
        // Invariant I11: a capability claim's status is derived by the
        // semantic closure scanner, not hand-written. The JcsCapability
        // record only exposes Partial/Unsupported factories publicly; the
        // Guaranteed factory is Core-internal (scanner-only). Freeze that
        // surface so adapters cannot smuggle a Guaranteed claim.
        var publicFactories = typeof(JcsCapability)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(JcsCapability))
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains("Partial", publicFactories);
        Assert.Contains("Unsupported", publicFactories);
        Assert.DoesNotContain(publicFactories, name =>
            name.Contains("Guaranteed", StringComparison.Ordinal));
    }
}
