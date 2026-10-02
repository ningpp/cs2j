using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Locks the registered pass sequence to the historical ConvertProject chain.
/// The fifteen hand-written blocks were replaced by the registry-driven loop, so
/// the topological sort must keep producing this exact order. Every
/// implemented pass is default-on except the gated lexical-normalize,
/// partial-merge and the M2 closure-lift (default-off pending M3 review);
/// the legacy flags survive as --no-xxx negations.
/// </summary>
public sealed class PassChainOrderTests
{
    private static readonly string[] LockedChain =
    [
        PassIds.Conditional,
        PassStubCatalog.Ids.LexicalNormalize,
        PassStubCatalog.Ids.PartialMerge,
        PassIds.LocalFunctionLift,
        PassIds.NullCoalesce,
        PassStubCatalog.Ids.ExpressionDesugar,
        PassIds.Enum,
        PassIds.EnumDeclaration,
        PassIds.Delegate,
        PassIds.Tuple,
        PassIds.ForeachDeconstruction,
        PassStubCatalog.Ids.ForeachSource,
        PassIds.PatternDesugar,
        PassIds.StripDefault,
        PassIds.RefOutIn,
        PassIds.Unsigned,
        PassStubCatalog.Ids.NumericBoundary,
        PassIds.Generic,
        PassIds.GenericDeepen,
        PassIds.RecordReadOnly,
        PassIds.ArrayMethod,
        PassIds.IndexRange,
        PassIds.MakeReadonly,
        PassIds.StructValueSemantics,
        PassStubCatalog.Ids.UnsafeFfm,
        PassStubCatalog.Ids.ClosureLift,
        PassStubCatalog.Ids.IteratorLowering,
        PassIds.EliminateGoto,
        // C32 翻转的拓扑副作用：member-desugar 的 RunAfter closure-lift 进入
        // implemented 区后，它与只依赖 tuple 的 equality-concat 在 Kahn
        // 声明序破平中先后对调（两者无相互依赖；三工程回归全绿即行为无外溢）。
        PassIds.MemberDesugar,
        PassStubCatalog.Ids.EqualityConcat,
        PassIds.JavaKeywords,
    ];

    // lexical-normalize / partial-merge land Implemented/default-off (gated);
    // closure-lift ships M2 default-off pending the M3 enable-by-default review.
    private static readonly string[] EnabledByDefault =
        LockedChain.Where(id =>
            id != PassStubCatalog.Ids.LexicalNormalize
            && id != PassStubCatalog.Ids.PartialMerge
            && id != PassStubCatalog.Ids.ClosureLift).ToArray();

    [Fact]
    public void Registry_order_matches_the_locked_historical_chain()
    {
        var registry = PassCatalog.BuildRegistry();

        // Stubs interleave at their anchors but never execute; the implemented
        // subset must keep the exact historical sequence.
        Assert.Equal(
            LockedChain,
            registry.Ordered.Where(p => p.Status == PassStatus.Implemented).Select(p => p.Id).ToArray());

        var ordered = registry.Ordered.Where(p => p.Status == PassStatus.Implemented).ToList();
        Assert.True(
            ordered.FindIndex(p => p.Id == PassIds.Conditional)
                < ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.LexicalNormalize)
                && ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.LexicalNormalize)
                < ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.PartialMerge)
                && ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.PartialMerge)
                < ordered.FindIndex(p => p.Id == PassIds.LocalFunctionLift),
            "expected conditional < lexical-normalize < partial-merge < local-function-lift");

        Assert.True(
            ordered.FindIndex(p => p.Id == PassStubCatalog.Ids.PartialMerge)
                < ordered.FindIndex(p => p.Id == PassIds.MemberDesugar),
            "expected partial-merge < member-desugar (catalog: C46 after C20)");
    }

    [Fact]
    public void Java_keywords_remains_the_last_pass_with_stubs_registered()
    {
        var registry = PassCatalog.BuildRegistry();

        Assert.Equal(PassIds.JavaKeywords, registry.Ordered[^1].Id);
    }

    [Fact]
    public void Default_enablement_matches_the_legacy_flag_defaults()
    {
        var registry = PassCatalog.BuildRegistry();

        Assert.Equal(
            EnabledByDefault,
            registry.Ordered.Where(p => p.DefaultEnabled).Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Resolver_without_toggles_returns_default_passes_in_locked_order()
    {
        var plan = PassPlanResolver.Resolve(PassCatalog.BuildRegistry());

        Assert.True(plan.IsValid);
        Assert.Equal(EnabledByDefault, plan.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void UnsafeFfm_is_present_in_the_default_plan()
    {
        var plan = PassPlanResolver.Resolve(PassCatalog.BuildRegistry());

        Assert.True(plan.IsValid);
        Assert.Contains(
            plan.Enabled, p => p.Id == PassStubCatalog.Ids.UnsafeFfm);
    }

    [Fact]
    public void UnsafeFfm_when_enabled_runs_in_its_locked_slot_after_struct_value_semantics()
    {
        var registry = PassCatalog.BuildRegistry();
        var allOff = LockedChain.ToDictionary(id => id, _ => false);

        // --enable-pass layers ON TOP of the all-default-on chain, so to view
        // the isolated slot every other pass is switched off via legacy
        // toggles. With make-readonly + iterator-lowering enabled and the
        // anchor struct-value-semantics absent, unsafe-ffm still occupies its
        // registry slot: make-readonly → unsafe-ffm → iterator.
        var isolatedToggles = new Dictionary<string, bool>(allOff)
        {
            [PassIds.MakeReadonly] = true,
            [PassStubCatalog.Ids.IteratorLowering] = true,
        };
        var plan = PassPlanResolver.Resolve(
            registry,
            isolatedToggles,
            enablePassIds: new[] { PassStubCatalog.Ids.UnsafeFfm });

        Assert.True(plan.IsValid);
        Assert.Equal(new[]
        {
            PassIds.MakeReadonly,
            PassStubCatalog.Ids.UnsafeFfm,
            PassStubCatalog.Ids.IteratorLowering,
        }, plan.Enabled.Select(p => p.Id).ToArray());

        // When the anchor is co-enabled, the adjacent slot is
        // struct-value-semantics → unsafe-ffm.
        var withAnchor = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[]
            {
                PassIds.StructValueSemantics,
                PassStubCatalog.Ids.UnsafeFfm,
            });

        Assert.True(withAnchor.IsValid);
        Assert.Equal(new[]
        {
            PassIds.StructValueSemantics,
            PassStubCatalog.Ids.UnsafeFfm,
        }, withAnchor.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void ClosureLift_when_enabled_runs_in_its_locked_slot_after_ref_out_in_before_iterator()
    {
        var registry = PassCatalog.BuildRegistry();
        var allOff = LockedChain.ToDictionary(id => id, _ => false);

        // Default-off: absent from the default plan even with every legacy
        // toggle at its baseline.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.DoesNotContain(baseline.Enabled, p => p.Id == PassStubCatalog.Ids.ClosureLift);

        // Isolated slot: ref-out-in → closure-lift → iterator-lowering.
        var isolatedToggles = new Dictionary<string, bool>(allOff)
        {
            [PassIds.RefOutIn] = true,
            [PassStubCatalog.Ids.IteratorLowering] = true,
        };
        var plan = PassPlanResolver.Resolve(
            registry,
            isolatedToggles,
            enablePassIds: new[] { PassStubCatalog.Ids.ClosureLift });

        Assert.True(plan.IsValid);
        Assert.Equal(new[]
        {
            PassIds.RefOutIn,
            PassStubCatalog.Ids.ClosureLift,
            PassStubCatalog.Ids.IteratorLowering,
        }, plan.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Resolver_legacy_toggles_can_disable_and_enable_any_pass()
    {
        var toggles = LockedChain.ToDictionary(id => id, _ => false);
        toggles[PassIds.RecordReadOnly] = true;

        var plan = PassPlanResolver.Resolve(PassCatalog.BuildRegistry(), toggles);

        Assert.True(plan.IsValid);
        Assert.Equal(new[] { PassIds.RecordReadOnly }, plan.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Resolver_explicit_enable_and_disabled_override_legacy_toggles()
    {
        var toggles = LockedChain.ToDictionary(id => id, _ => false);

        var plan = PassPlanResolver.Resolve(
            PassCatalog.BuildRegistry(),
            toggles,
            enablePassIds: new[] { PassIds.ArrayMethod },
            disablePassIds: new[] { PassIds.Enum });

        Assert.True(plan.IsValid);
        var enabled = plan.Enabled.Select(p => p.Id).ToArray();
        Assert.Equal(new[] { PassIds.ArrayMethod }, enabled);
    }

    [Fact]
    public void Resolver_rejects_unknown_pass_ids_with_CS2J9002()
    {
        var plan = PassPlanResolver.Resolve(
            PassCatalog.BuildRegistry(),
            enablePassIds: new[] { "does-not-exist" });

        Assert.False(plan.IsValid);
        var error = Assert.Single(plan.Errors);
        Assert.StartsWith(PreprocessDiagnosticCodes.UnknownPass, error);
        Assert.Contains("does-not-exist", error);
    }

    [Fact]
    public void Resolver_rejects_enable_and_disable_conflicts_with_CS2J9002()
    {
        var plan = PassPlanResolver.Resolve(
            PassCatalog.BuildRegistry(),
            enablePassIds: new[] { PassIds.Enum },
            disablePassIds: new[] { PassIds.Enum });

        Assert.False(plan.IsValid);
        var error = Assert.Single(plan.Errors);
        Assert.StartsWith(PreprocessDiagnosticCodes.UnknownPass, error);
        Assert.Contains(PassIds.Enum, error);
    }

    [Fact]
    public void Implemented_catalog_passes_carry_their_catalog_item_mappings()
    {
        var byId = PassCatalog.BuildRegistry().Ordered.ToDictionary(p => p.Id);

        Assert.Equal("C01", byId[PassIds.RefOutIn].CatalogItems);
        Assert.Equal("C02", byId[PassIds.GenericDeepen].CatalogItems);
        Assert.Equal("C03, C37", byId[PassIds.PatternDesugar].CatalogItems);
        Assert.Equal("C04", byId[PassStubCatalog.Ids.IteratorLowering].CatalogItems);
        Assert.Equal("C15, C34, C17", byId[PassStubCatalog.Ids.NumericBoundary].CatalogItems);
        Assert.Equal("C07", byId[PassIds.StructValueSemantics].CatalogItems);
        Assert.Equal("C05", byId[PassIds.EnumDeclaration].CatalogItems);
        Assert.Equal("C06", byId[PassStubCatalog.Ids.UnsafeFfm].CatalogItems);
        Assert.Equal("C11, C12, C13, C14, C39", byId[PassIds.MemberDesugar].CatalogItems);
        Assert.Equal("C16, C40", byId[PassStubCatalog.Ids.ForeachSource].CatalogItems);
        Assert.True(byId[PassStubCatalog.Ids.ForeachSource].DefaultEnabled);
        Assert.Equal("C47", byId[PassStubCatalog.Ids.EqualityConcat].CatalogItems);
        Assert.Equal("C23, C36", byId[PassStubCatalog.Ids.ExpressionDesugar].CatalogItems);
        Assert.Equal("C20", byId[PassStubCatalog.Ids.PartialMerge].CatalogItems);
        Assert.False(byId[PassStubCatalog.Ids.PartialMerge].DefaultEnabled);
        Assert.Equal("C32", byId[PassStubCatalog.Ids.ClosureLift].CatalogItems);
        Assert.False(byId[PassStubCatalog.Ids.ClosureLift].DefaultEnabled);
    }

    [Fact]
    public void ChainFactory_creates_an_adapter_for_every_implemented_pass()
    {
        var config = new PassChainConfiguration
        {
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            MemberDesugarOptionsFactory = () => new CSharpToJava.Core.MemberDesugar.MemberDesugarOptions(),
            StructValueSemanticsOptionsFactory = () => new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
        };

        foreach (var descriptor in PassCatalog.BuildRegistry().Ordered
                     .Where(p => p.Status == PassStatus.Implemented))
        {
            var adapter = PassChainFactory.CreateAdapter(descriptor.Id, config);
            Assert.Equal(descriptor.Id, adapter.Descriptor.Id);
        }
    }

    [Fact]
    public void StructValueSemantics_is_default_on_and_disable_paths_are_equivalent()
    {
        var registry = PassCatalog.BuildRegistry();
        var baselineToggles = PassChainFactory.BuildLegacyToggles(new ConvertProjectOptions());
        var baseline = PassPlanResolver.Resolve(registry, baselineToggles);

        // Default chain (convert-project legacy toggles) contains C07.
        Assert.Contains(baseline.Enabled, p => p.Id == PassIds.StructValueSemantics);

        // convert-project 的 --struct-value-semantics=false 经 legacy toggle 关回。
        var viaLegacyOff = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildLegacyToggles(
                new ConvertProjectOptions { StructValueSemantics = false }));

        // preprocess 的 --disable-pass struct-value-semantics 走显式覆盖（§7.6 等价性）。
        var viaExplicitOff = PassPlanResolver.Resolve(
            registry,
            baselineToggles,
            disablePassIds: new[] { PassIds.StructValueSemantics });

        Assert.True(viaLegacyOff.IsValid);
        Assert.True(viaExplicitOff.IsValid);

        // 两种关闭方式产生完全相同的启用集合……
        Assert.Equal(
            viaLegacyOff.Enabled.Select(p => p.Id).ToArray(),
            viaExplicitOff.Enabled.Select(p => p.Id).ToArray());

        // ……且都恰为基线集合少掉 C07 一个 pass。
        var baselineIds = baseline.Enabled.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var expected = registry.Ordered
            .Select(p => p.Id)
            .Where(id => baselineIds.Contains(id) && id != PassIds.StructValueSemantics)
            .ToArray();
        Assert.Equal(expected, viaExplicitOff.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void EqualityConcat_is_default_on_and_disable_paths_are_equivalent()
    {
        var registry = PassCatalog.BuildRegistry();
        var baselineToggles = PassChainFactory.BuildLegacyToggles(new ConvertProjectOptions());
        var baseline = PassPlanResolver.Resolve(registry, baselineToggles);

        // Default chain (convert-project legacy toggles) contains C47.
        Assert.Contains(baseline.Enabled, p => p.Id == PassStubCatalog.Ids.EqualityConcat);

        // convert-project: --no-equality-concat 经 legacy toggle 关回。
        var viaLegacyOff = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildLegacyToggles(
                new ConvertProjectOptions { NoEqualityConcat = true }));

        // generic: --disable-pass equality-concat.
        var viaExplicitOff = PassPlanResolver.Resolve(
            registry,
            baselineToggles,
            disablePassIds: new[] { PassStubCatalog.Ids.EqualityConcat });

        Assert.True(viaLegacyOff.IsValid);
        Assert.True(viaExplicitOff.IsValid);
        Assert.Equal(
            viaLegacyOff.Enabled.Select(p => p.Id).ToArray(),
            viaExplicitOff.Enabled.Select(p => p.Id).ToArray());

        // Explicit re-enabling against the legacy-off toggle restores the baseline set.
        var reEnabled = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildLegacyToggles(
                new ConvertProjectOptions { NoEqualityConcat = true }),
            enablePassIds: new[] { PassStubCatalog.Ids.EqualityConcat });
        Assert.True(reEnabled.IsValid);
        Assert.Equal(
            baseline.Enabled.Select(p => p.Id).ToArray(),
            reEnabled.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void BuildLegacyToggles_covers_every_registered_pass()
    {
        var options = new ConvertProjectOptions();
        var toggles = PassChainFactory.BuildLegacyToggles(options);

        // Legacy flags only exist for the historical implemented passes; stubs and
        // newly flipped registry passes (e.g. generic-deepen, unsafe-ffm,
        // C36 expression-desugar) are reachable solely through their descriptor
        // default plus
        // --enable-pass/--pass.
        Assert.Equal(
            LockedChain.Where(id => id != PassIds.GenericDeepen && id != PassIds.PatternDesugar
                && id != PassIds.EnumDeclaration
                && id != PassStubCatalog.Ids.UnsafeFfm
                && id != PassStubCatalog.Ids.ExpressionDesugar
                && id != PassStubCatalog.Ids.LexicalNormalize
                && id != PassStubCatalog.Ids.PartialMerge
                && id != PassStubCatalog.Ids.ClosureLift)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            toggles.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }
}
