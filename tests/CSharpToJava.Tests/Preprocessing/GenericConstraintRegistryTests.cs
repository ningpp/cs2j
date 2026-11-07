using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>Registry/contract for the R2/CN <c>generic-constraint</c> pass (§6.5).</summary>
public sealed class GenericConstraintRegistryTests
{
    private static PassChainConfiguration CreateConfig() => new()
    {
        MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
        RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
        JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
        MemberDesugarOptionsFactory = () => new CSharpToJava.Core.MemberDesugar.MemberDesugarOptions(),
        StructValueSemanticsOptionsFactory = () => new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
        VarianceAuditOptionsFactory = () => new CSharpToJava.Core.VarianceAudit.VarianceAuditOptions(),
        EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
        LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
        EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
        UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
        ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
        LinqChannelOptionsFactory = () => new CSharpToJava.Core.LinqChannel.LinqChannelOptions(),
    };

    [Fact]
    public void Descriptor_IsImplementedDefaultOn_AnchoredOnGeneric()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.GenericConstraint);

        Assert.Equal("generic-constraint", descriptor.Id);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(".cs2j-generic-constraint-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Abi, descriptor.Stage);
        Assert.Equal(new[] { PassIds.Generic }, descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
    }

    [Fact]
    public void Contract_OwnsTheConstraintConstructAndDeclaresJcsGeneric()
    {
        var contract = PassCatalog.ImplementedById(PassIds.GenericConstraint).Contract;

        Assert.NotNull(contract);
        Assert.Contains(PassConstructIds.GenericConstraint, contract!.Consumes);
        Assert.Contains(PassConstructIds.GenericConstraint, contract.Postconditions);
        Assert.Contains(PassConstructIds.GenericConstraint, contract.MayLeave);
        Assert.Equal(ResidualPolicy.PreserveAndWarn, contract.Policy);
        Assert.True(contract.Idempotent);
        Assert.True(contract.RequiresWholeTree);
        Assert.Contains("JCS.GENERIC", contract.EffectiveCapabilityIds);
    }

    [Fact]
    public void ChainOrder_IsGenericThenConstraintThenThreadStaticThenDeepen()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered.Select(pass => pass.Id).ToList();
        var constraint = ordered.IndexOf(PassIds.GenericConstraint);

        Assert.True(constraint > 0, "generic-constraint must be registered");
        Assert.Equal(PassIds.Generic, ordered[constraint - 1]);
        Assert.Equal(PassIds.ThreadStatic, ordered[constraint + 1]);
        Assert.Equal(PassIds.GenericDeepen, ordered[constraint + 2]);

        Assert.Contains(PassIds.GenericConstraint, PassCatalog.ImplementedById(PassIds.ThreadStatic).RunAfterIds);
        Assert.Contains(PassIds.GenericConstraint, PassCatalog.ImplementedById(PassIds.GenericDeepen).RunAfterIds);
    }

    [Fact]
    public void DefaultPlanRunsIt_DisableOptsOut()
    {
        var registry = PassCatalog.BuildRegistry();

        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(baseline.Enabled, pass => pass.Id == PassIds.GenericConstraint);

        var disabled = PassPlanResolver.Resolve(
            registry, disablePassIds: new[] { PassIds.GenericConstraint });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(disabled.Enabled, pass => pass.Id == PassIds.GenericConstraint);

        // Registry-default-only pass: no legacy toggle entry.
        Assert.DoesNotContain(
            PassIds.GenericConstraint,
            PassChainFactory.BuildLegacyToggles(new ConvertProjectOptions()).Keys);
    }

    [Fact]
    public void Factory_CreatesTheAdapter()
    {
        var adapter = PassChainFactory.CreateAdapter(PassIds.GenericConstraint, CreateConfig());

        Assert.Equal(PassIds.GenericConstraint, adapter.Descriptor.Id);
        Assert.True(adapter.VerifyBuild);
        Assert.True(adapter.VerifyTests);
        Assert.True(adapter.DeleteBuildOutputsAfterRestore);
    }
}
