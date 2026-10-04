using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Registry contract for the C19 extension-methods M0 flip: stub entry removed,
/// implemented descriptor installed in the iterator-lowering → eliminate-goto
/// slot, default kept OFF, verify-build/tests ON.
/// </summary>
public sealed class ExtensionMethodRegistryTests
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
    public void ExtensionMethods_Is_Implemented_WithC19AndC49CatalogItems_AndDefaultOn()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.ExtensionMethods);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.Equal("C19, C49", descriptor.CatalogItems);
        Assert.Equal(".cs2j-extension-methods-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Expression, descriptor.Stage);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
    }

    [Fact]
    public void ExtensionMethods_RunAfter_CarriesAllSevenAnchors()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.ExtensionMethods);
        Assert.Equal(new[]
        {
            PassIds.Delegate,
            PassIds.Tuple,
            PassIds.RefOutIn,
            PassIds.Unsigned,
            PassIds.ArrayMethod,
            PassIds.MakeReadonly,
            PassStubCatalog.Ids.IteratorLowering,
        }, descriptor.RunAfterIds);
    }

    [Fact]
    public void Registry_PlacesExtensionMethods_BetweenIteratorLoweringAndEliminateGoto()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered.Select(p => p.Id).ToList();
        var index = ordered.IndexOf(PassIds.ExtensionMethods);
        Assert.True(index > 0, "extension-methods must be registered");
        Assert.Equal(PassStubCatalog.Ids.IteratorLowering, ordered[index - 1]);
        Assert.Equal(PassIds.EliminateGoto, ordered[index + 1]);
    }

    [Fact]
    public void ExtensionMethods_StubEntry_IsGone_AndIdResolvesToImplementedDescriptor()
    {
        var registry = PassCatalog.BuildRegistry();
        // The single registered entry for "extension-methods" is Implemented
        // (the stub row and the Ids.ExtensionMethods stub constant were removed).
        var descriptor = registry.Require(PassIds.ExtensionMethods);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.Single(registry.Ordered.Where(p => p.Id == PassIds.ExtensionMethods));
    }

    [Fact]
    public void ExtensionMembers_IsConsolidated_AndLinqChannel_StillAnchorsAfterExtensionMethods()
    {
        var registry = PassCatalog.BuildRegistry();

        // C49 lives inside the extension-methods pass; no separate
        // extension-members entry is registered under either name.
        Assert.DoesNotContain(
            registry.Ordered, p => p.Id is "extension-members");
        var descriptor = registry.Require(PassIds.ExtensionMethods);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.Equal("C19, C49", descriptor.CatalogItems);

        // The linq-channel stub keeps its anchor after the merged pass.
        var linqChannel = registry.Require(PassStubCatalog.Ids.LinqChannel);
        Assert.Equal(PassStatus.Stub, linqChannel.Status);
        Assert.Contains(PassIds.ExtensionMethods, linqChannel.RunAfterIds);
    }

    [Fact]
    public void Factory_CreatesAdapter_ForExtensionMethods_WithLatestParseOptions()
    {
        var adapter = PassChainFactory.CreateAdapter(PassIds.ExtensionMethods, CreateConfig());
        Assert.Equal(PassIds.ExtensionMethods, adapter.Descriptor.Id);
        Assert.True(adapter.VerifyBuild);
        Assert.True(adapter.VerifyTests);
        Assert.True(adapter.DeleteBuildOutputsAfterRestore);
        Assert.Equal(LanguageVersion.Latest, adapter.CreateParseOptions(null!).SpecifiedLanguageVersion);
    }

    [Fact]
    public void ExtensionMethods_InDefaultPlan_WithLockedSlotBetweenIteratorLoweringAndEliminateGoto()
    {
        var registry = PassCatalog.BuildRegistry();

        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(baseline.Enabled, p => p.Id == PassIds.ExtensionMethods);

        // Locked slot: make-readonly → iterator-lowering → extension-methods.
        var ordered = registry.Ordered.Select(p => p.Id).ToList();
        var index = ordered.IndexOf(PassIds.ExtensionMethods);
        Assert.Equal(PassStubCatalog.Ids.IteratorLowering, ordered[index - 1]);
        Assert.Equal(PassIds.EliminateGoto, ordered[index + 1]);
    }
}
