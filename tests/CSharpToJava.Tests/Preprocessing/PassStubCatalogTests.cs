using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Step 8: the merged-family stubs must be visible and disabled by default,
/// must never join the default plan, and must fail loudly (CS2J9001) the
/// moment they are enabled — without touching the filesystem.
/// </summary>
public sealed class PassStubCatalogTests
{
    private static PassDescriptor[] Stubs =>
        PassCatalog.BuildRegistry().WhereStatus(PassStatus.Stub).ToArray();

    [Fact]
    public void PatternDesugar_IsImplemented_AndNotAStub()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.PatternDesugar);

        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.Equal("C03, C37", descriptor.CatalogItems);
        Assert.Equal(".cs2j-pattern-desugar-src", descriptor.IntermediateDirectoryName);
        Assert.DoesNotContain(Stubs, stub => stub.Id == PassIds.PatternDesugar);
    }

    [Fact]
    public void PlatformIsolation_IsImplemented_AndNotAStub()
    {
        var descriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.PlatformIsolation);

        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(".cs2j-platform-isolation-src", descriptor.IntermediateDirectoryName);
        Assert.Equal("C42", descriptor.CatalogItems);
        Assert.DoesNotContain(Stubs, stub => stub.Id == PassStubCatalog.Ids.PlatformIsolation);
    }

    [Fact]
    public void DynamicDesugar_IsImplemented_AndNotAStub()
    {
        var descriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.DynamicDesugar);

        // C25 M1 flipped the stub to Implemented; AUDIT was flipped
        // default-on 2026-10-06 after the §8 audit gates (Rewrite stays
        // opt-in via --dynamic-mode Rewrite).
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(".cs2j-dynamic-desugar-src", descriptor.IntermediateDirectoryName);
        Assert.Equal("C25", descriptor.CatalogItems);
        Assert.DoesNotContain(Stubs, stub => stub.Id == PassStubCatalog.Ids.DynamicDesugar);
    }

    [Fact]
    public void Every_stub_is_disabled_by_default()
    {
        Assert.All(Stubs, stub => Assert.False(
            stub.DefaultEnabled,
            $"stub '{stub.Id}' must stay opt-in until implemented"));
    }

    [Fact]
    public void Every_stub_has_a_unique_intermediate_directory()
    {
        var directories = Stubs.Select(p => p.IntermediateDirectoryName).ToArray();

        Assert.Equal(directories.Length, directories.Distinct(StringComparer.Ordinal).Count());
        Assert.All(directories, directory => Assert.StartsWith(".cs2j-", directory));
        Assert.All(directories, directory => Assert.EndsWith("-src", directory));
    }

    [Fact]
    public void Default_plan_contains_no_stubs()
    {
        var plan = PassPlanResolver.Resolve(PassCatalog.BuildRegistry());

        Assert.True(plan.IsValid);
        Assert.DoesNotContain(plan.Enabled, pass => pass.Status == PassStatus.Stub);
    }

    [Fact]
    public void LinqChannel_IsImplemented_AndNotAStub()
    {
        var descriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.LinqChannel);

        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled, "C26 M4 flipped linq-channel default-on (2026-10-06)");
        Assert.Equal(PassStage.ControlFlow, descriptor.Stage);
        Assert.Equal(".cs2j-linq-channel-src", descriptor.IntermediateDirectoryName);
        Assert.Equal("C26", descriptor.CatalogItems);
        Assert.Equal(
            new[]
            {
                PassIds.ExtensionMethods,
                PassIds.AnonymousTypeLift,
                PassIds.EliminateGoto,
            },
            descriptor.RunAfterIds);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.DoesNotContain(Stubs, stub => stub.Id == PassStubCatalog.Ids.LinqChannel);
        Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.LinqChannelPassAdapter));
    }

    [Fact]
    public void LinqChannel_IsInTheDefaultPlan_AndPlansAsImplementedWhenExplicitlySelected()
    {
        var registry = PassCatalog.BuildRegistry();

        // M4 (2026-10-06): default-on — the baseline plan contains the channel.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(baseline.Enabled, pass => pass.Id == PassStubCatalog.Ids.LinqChannel);

        // Standalone --pass selection still plans the single id as Implemented.
        var enabled = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildAllDisabledToggles(registry),
            enablePassIds: new[] { PassStubCatalog.Ids.LinqChannel });
        Assert.True(enabled.IsValid);
        var entry = Assert.Single(enabled.Enabled);
        Assert.Equal(PassStubCatalog.Ids.LinqChannel, entry.Id);
        Assert.Equal(PassStatus.Implemented, entry.Status);
    }

    // 注：最后一个 Stub 样本 DynamicDesugar（C25 M1）翻为 Implemented 后，
    // PassStubCatalog.Stubs 已为空——"启用 Stub → CS2J9001"的两个样本测试随之移除
    // （机制 CliPreprocessChain/CS2J9001 保留；再有 Stub 注册时补同样本）。
}
