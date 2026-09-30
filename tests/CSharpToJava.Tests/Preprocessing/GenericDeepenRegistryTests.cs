using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>Registry contract for the generic-deepen (C02) pass flip.</summary>
public sealed class GenericDeepenRegistryTests
{
    private static PassChainConfiguration CreateConfig() => new()
    {
        MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
        RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
        JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
        MemberDesugarOptionsFactory = () => new CSharpToJava.Core.MemberDesugar.MemberDesugarOptions(),
    };

    [Fact]
    public void GenericDeepen_Is_Implemented_WithC02CatalogItems()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.GenericDeepen);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.Equal("C02", descriptor.CatalogItems);
        Assert.Equal(".cs2j-generic-deepen-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Abi, descriptor.Stage);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(new[] { PassIds.Generic }, descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
    }

    [Fact]
    public void Registry_PlacesGenericDeepen_BetweenGenericAndRecordReadOnly()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered.Select(p => p.Id).ToList();
        var gd = ordered.IndexOf(PassIds.GenericDeepen);
        Assert.True(gd > 0, "generic-deepen must be registered");
        Assert.Equal(PassIds.Generic, ordered[gd - 1]);
        Assert.Equal(PassIds.RecordReadOnly, ordered[gd + 1]);
    }

    [Fact]
    public void C30_C33_HaveIndependentStubs()
    {
        var registry = PassCatalog.BuildRegistry();
        var closed = registry.Require(PassStubCatalog.Ids.ClosedMetadata);
        var variance = registry.Require(PassStubCatalog.Ids.VarianceAudit);
        Assert.Equal(PassStatus.Stub, closed.Status);
        Assert.Equal("C30", closed.CatalogItems);
        Assert.Contains(PassIds.GenericDeepen, closed.RunAfterIds);
        Assert.Equal(PassStatus.Stub, variance.Status);
        Assert.Equal("C33", variance.CatalogItems);
    }

    [Fact]
    public void AttributeNormalize_StillAnchorsAfterGenericDeepen()
    {
        var attribute = PassCatalog.BuildRegistry().Require(PassStubCatalog.Ids.AttributeNormalize);
        Assert.Contains(PassIds.GenericDeepen, attribute.RunAfterIds);
    }

    [Fact]
    public void Factory_CreatesAdapter_ForGenericDeepen()
    {
        var adapter = PassChainFactory.CreateAdapter(PassIds.GenericDeepen, CreateConfig());
        Assert.Equal(PassIds.GenericDeepen, adapter.Descriptor.Id);
        Assert.True(adapter.VerifyBuild);
        Assert.True(adapter.VerifyTests);
    }
}
