using System.Reflection;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// M5.3-a: the NORM construct ids, the residual registry and the pass contracts
/// share one source of truth.
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
}
