using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// C18 async-normalize Stub→Implemented flip registry locks (design §3.2/§3.4):
/// Kahn slot around iterator-lowering/extension-methods/eliminate-goto, the
/// stub-count shrink, the default-on plan behavior and the descriptor shape.
/// Adapter construction for every implemented pass (this one included) is
/// covered by PassChainOrderTests.ChainFactory_creates_an_adapter_for_every_implemented_pass.
/// </summary>
public sealed class AsyncNormalizeOrderTests
{
    [Fact]
    public void Registry_places_async_normalize_after_make_readonly_and_iterator_before_eliminate_goto()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered.ToList();

        var makeReadonly = ordered.FindIndex(p => p.Id == PassIds.MakeReadonly);
        var iterator = ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.IteratorLowering);
        var asyncNormalize = ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.AsyncNormalize);
        var eliminateGoto = ordered.FindIndex(p => p.Id == PassIds.EliminateGoto);

        Assert.True(makeReadonly >= 0 && iterator >= 0 && asyncNormalize >= 0 && eliminateGoto >= 0);
        Assert.True(makeReadonly < asyncNormalize,
            "expected make-readonly < async-normalize");
        Assert.True(iterator < asyncNormalize,
            "expected iterator-lowering < async-normalize (declaration-order tiebreak)");
        Assert.True(asyncNormalize < eliminateGoto,
            "expected async-normalize < eliminate-goto");
    }

    [Fact]
    public void Flip_leaves_three_stubs()
    {
        // Remaining stubs: attribute-normalize, linq-channel,
        // dynamic-desugar (extension-members was consolidated into
        // extension-methods; anonymous-type-lift and async-normalize are
        // both Implemented; thread-static flipped Implemented/default-off
        // in C09 M1).
        var stubs = PassCatalog.BuildRegistry().WhereStatus(PassStatus.Stub).ToArray();

        Assert.Equal(3, stubs.Length);
        Assert.DoesNotContain(stubs, p => p.Id == PassStubCatalog.Ids.AsyncNormalize);
        Assert.DoesNotContain(stubs, p => p.Id == PassIds.ThreadStatic);
    }

    [Fact]
    public void Default_plan_includes_async_normalize_and_enabling_it_plans_it_as_Implemented()
    {
        var registry = PassCatalog.BuildRegistry();

        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(
            baseline.Enabled, p => p.Id == PassStubCatalog.Ids.AsyncNormalize);

        // Explicit opt-in with every toggle off still plans it as Implemented.
        var allOff = PassChainFactory.BuildAllDisabledToggles(registry);
        var plan = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[] { PassStubCatalog.Ids.AsyncNormalize });

        Assert.True(plan.IsValid);
        var planned = Assert.Single(plan.Enabled);
        Assert.Equal(PassStubCatalog.Ids.AsyncNormalize, planned.Id);
        Assert.Equal(PassStatus.Implemented, planned.Status);
    }

    [Fact]
    public void Implemented_descriptor_is_default_on_semantic_verified_StateMachine_C18()
    {
        var descriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.AsyncNormalize);

        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
        Assert.Equal(PassStage.StateMachine, descriptor.Stage);
        Assert.Equal("C18, C38 (C44 subtopic)", descriptor.CatalogItems);
        Assert.Equal(".cs2j-async-normalize-src", descriptor.IntermediateDirectoryName);
    }
}
