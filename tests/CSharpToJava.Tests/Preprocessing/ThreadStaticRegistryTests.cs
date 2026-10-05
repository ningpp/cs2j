using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Registry contract for the C09 thread-static M1 stub→Implemented flip
/// (design §3.1/§7.5): Implemented but default-off, descriptor shape,
/// generic → thread-static → generic-deepen slot lock and resolver behavior.
/// </summary>
public sealed class ThreadStaticRegistryTests
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
    public void ThreadStatic_Is_Implemented_DefaultOff_WithC09CatalogItems()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub row is gone and the id resolves to a single Implemented
        // descriptor (the CS2J9001 stub-enable path no longer applies).
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassIds.ThreadStatic && pass.Status == PassStatus.Stub);
        Assert.Single(registry.Ordered.Where(pass => pass.Id == PassIds.ThreadStatic));

        var descriptor = PassCatalog.ImplementedById(PassIds.ThreadStatic);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.False(descriptor.DefaultEnabled);
        Assert.Equal(".cs2j-thread-static-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Declarative, descriptor.Stage);
        Assert.Equal(new[] { PassIds.Generic }, descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
        Assert.Equal("C09", descriptor.CatalogItems);
        Assert.Equal(
            "Rewrite [ThreadStatic] fields into explicit ThreadLocal<T> holders with .Value access.",
            descriptor.Description);
    }

    [Fact]
    public void ThreadStatic_IsAbsentFromDefaultPlan_ButEnableAndDisableResolve()
    {
        var registry = PassCatalog.BuildRegistry();

        // Default-off: absent from the default plan.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.DoesNotContain(baseline.Enabled, pass => pass.Id == PassIds.ThreadStatic);

        // Explicit disable against the baseline stays valid and omits it.
        var disabled = PassPlanResolver.Resolve(
            registry,
            disablePassIds: new[] { PassIds.ThreadStatic });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(disabled.Enabled, pass => pass.Id == PassIds.ThreadStatic);

        // Explicit opt-in with every toggle off plans it alone as Implemented.
        var enabled = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildAllDisabledToggles(registry),
            enablePassIds: new[] { PassIds.ThreadStatic });
        Assert.True(enabled.IsValid);
        var entry = Assert.Single(enabled.Enabled);
        Assert.Equal(PassIds.ThreadStatic, entry.Id);
        Assert.Equal(PassStatus.Implemented, entry.Status);
    }

    [Fact]
    public void Registry_PlacesThreadStatic_BetweenGenericAndGenericDeepen()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered.Select(pass => pass.Id).ToList();
        var ts = ordered.IndexOf(PassIds.ThreadStatic);

        Assert.True(ts > 0, "thread-static must be registered");
        Assert.Equal(PassIds.Generic, ordered[ts - 1]);
        Assert.Equal(PassIds.GenericDeepen, ordered[ts + 1]);
    }

    [Fact]
    public void Factory_CreatesAdapter_ForThreadStatic()
    {
        var adapter = PassChainFactory.CreateAdapter(PassIds.ThreadStatic, CreateConfig());

        Assert.Equal(PassIds.ThreadStatic, adapter.Descriptor.Id);
        Assert.True(adapter.VerifyBuild);
        Assert.True(adapter.VerifyTests);
        Assert.True(adapter.DeleteBuildOutputsAfterRestore);
    }
}
