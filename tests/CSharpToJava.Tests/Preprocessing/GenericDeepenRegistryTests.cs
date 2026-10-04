using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;

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
        StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
        EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
        LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
        EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
        UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
        ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
        VarianceAuditOptionsFactory = () => new CSharpToJava.Core.VarianceAudit.VarianceAuditOptions(),
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
    public void Registry_PlacesGenericDeepen_BeforeClosedMetadataAndRecordReadOnly()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered.Select(p => p.Id).ToList();
        var gd = ordered.IndexOf(PassIds.GenericDeepen);
        Assert.True(gd > 0, "generic-deepen must be registered");
        Assert.Equal(PassIds.Generic, ordered[gd - 1]);
        // C30 M2' + C33 M1a: closed-metadata and variance-audit both flipped
        // to Implemented, slotting in after generic-deepen (design §3.4/§4.3/§5.5).
        Assert.Equal(PassIds.ClosedMetadata, ordered[gd + 1]);
        Assert.Equal(PassIds.VarianceAudit, ordered[gd + 2]);
        Assert.Equal(PassIds.RecordReadOnly, ordered[gd + 3]);
    }

    [Fact]
    public void C30_AndC33_AreImplemented_AfterGenericDeepen()
    {
        var registry = PassCatalog.BuildRegistry();
        var closed = registry.Require(PassIds.ClosedMetadata);
        Assert.Equal(PassStatus.Implemented, closed.Status);
        Assert.Equal("C30", closed.CatalogItems);
        Assert.Contains(PassIds.GenericDeepen, closed.RunAfterIds);

        var variance = PassCatalog.ImplementedById(PassIds.VarianceAudit);
        Assert.Equal(PassStatus.Implemented, variance.Status);
        Assert.Equal("C33", variance.CatalogItems);
        Assert.Equal(".cs2j-variance-audit-src", variance.IntermediateDirectoryName);
        Assert.Equal(PassStage.Declarative, variance.Stage);
        Assert.True(variance.DefaultEnabled);
        Assert.Equal(new[] { PassIds.Generic }, variance.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, variance.SupportLibrary);
        Assert.True(variance.NeedsSemanticModel);
        Assert.False(variance.VerifyBuildByDefault);
        Assert.False(variance.VerifyTestsByDefault);
        Assert.DoesNotContain(registry.WhereStatus(PassStatus.Stub), stub => stub.Id == PassIds.VarianceAudit);
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
