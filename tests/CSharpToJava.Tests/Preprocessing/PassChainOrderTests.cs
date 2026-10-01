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
/// the topological sort must keep producing this exact order. As of 2026-09-30
/// every implemented pass is default-on ("default enablement 收口"); the legacy
/// flags survive as --no-xxx negations.
/// </summary>
public sealed class PassChainOrderTests
{
    private static readonly string[] LockedChain =
    [
        PassIds.Conditional,
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
        PassStubCatalog.Ids.IteratorLowering,
        PassIds.EliminateGoto,
        PassStubCatalog.Ids.EqualityConcat,
        PassIds.MemberDesugar,
        PassIds.JavaKeywords,
    ];

    // Opt-in passes stay excluded from the default chain: C07 struct-value-semantics,
    // C05 enum-declaration, C16/C40 foreach-source, C47 equality-concat,
    // C06 unsafe-ffm and C36 expression-desugar (each design's §8
    // default-enablement gate has not released them); every other implemented
    // id defaults on.
    private static readonly string[] EnabledByDefault =
        LockedChain.Where(id => id != PassIds.StructValueSemantics
            && id != PassIds.EnumDeclaration
            && id != PassStubCatalog.Ids.ForeachSource
            && id != PassStubCatalog.Ids.EqualityConcat
            && id != PassStubCatalog.Ids.UnsafeFfm
            && id != PassStubCatalog.Ids.ExpressionDesugar).ToArray();

    [Fact]
    public void Registry_order_matches_the_locked_historical_chain()
    {
        var registry = PassCatalog.BuildRegistry();

        // Stubs interleave at their anchors but never execute; the implemented
        // subset must keep the exact historical sequence.
        Assert.Equal(
            LockedChain,
            registry.Ordered.Where(p => p.Status == PassStatus.Implemented).Select(p => p.Id).ToArray());
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
    public void UnsafeFfm_is_absent_from_the_default_plan()
    {
        var plan = PassPlanResolver.Resolve(PassCatalog.BuildRegistry());

        Assert.True(plan.IsValid);
        Assert.DoesNotContain(
            plan.Enabled, p => p.Id == PassStubCatalog.Ids.UnsafeFfm);
    }

    [Fact]
    public void UnsafeFfm_when_enabled_runs_in_its_locked_slot_after_struct_value_semantics()
    {
        // RunAfter dependencies are not auto-enabled: with only unsafe-ffm
        // enabled the executed (filtered) order keeps it directly after
        // make-readonly (its anchor struct-value-semantics is absent but still
        // orders it in the full registry: make-readonly → unsafe-ffm → iterator).
        var plan = PassPlanResolver.Resolve(
            PassCatalog.BuildRegistry(),
            enablePassIds: new[] { PassStubCatalog.Ids.UnsafeFfm });

        Assert.True(plan.IsValid);
        var ids = plan.Enabled.Select(p => p.Id).ToArray();
        Assert.Equal(PassStubCatalog.Ids.UnsafeFfm,
            ids[Array.IndexOf(ids, PassIds.MakeReadonly) + 1]);
        Assert.Equal(PassStubCatalog.Ids.IteratorLowering,
            ids[Array.IndexOf(ids, PassStubCatalog.Ids.UnsafeFfm) + 1]);

        // When the anchor is co-enabled, the adjacent slot is
        // struct-value-semantics → unsafe-ffm.
        var withAnchor = PassPlanResolver.Resolve(
            PassCatalog.BuildRegistry(),
            enablePassIds: new[]
            {
                PassIds.StructValueSemantics,
                PassStubCatalog.Ids.UnsafeFfm,
            });

        Assert.True(withAnchor.IsValid);
        var anchored = withAnchor.Enabled.Select(p => p.Id).ToArray();
        Assert.Equal(PassStubCatalog.Ids.UnsafeFfm,
            anchored[Array.IndexOf(anchored, PassIds.StructValueSemantics) + 1]);
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
        Assert.Equal("C11, C12, C13, C14", byId[PassIds.MemberDesugar].CatalogItems);
        Assert.Equal("C16, C40", byId[PassStubCatalog.Ids.ForeachSource].CatalogItems);
        Assert.False(byId[PassStubCatalog.Ids.ForeachSource].DefaultEnabled);
        Assert.Equal("C47", byId[PassStubCatalog.Ids.EqualityConcat].CatalogItems);
        Assert.Equal("C36", byId[PassStubCatalog.Ids.ExpressionDesugar].CatalogItems);
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
    public void StructValueSemantics_legacy_flag_and_explicit_enable_are_equivalent()
    {
        var registry = PassCatalog.BuildRegistry();
        var baselineToggles = PassChainFactory.BuildLegacyToggles(new ConvertProjectOptions());
        var baseline = PassPlanResolver.Resolve(registry, baselineToggles);

        // convert-project 的 --struct-value-semantics 经 legacy toggle 进 Resolver。
        var viaLegacy = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildLegacyToggles(
                new ConvertProjectOptions { StructValueSemantics = true }));

        // preprocess 的 --enable-pass struct-value-semantics 走显式覆盖（§7.6 等价性）。
        var viaExplicit = PassPlanResolver.Resolve(
            registry,
            baselineToggles,
            enablePassIds: new[] { PassIds.StructValueSemantics });

        Assert.True(viaLegacy.IsValid);
        Assert.True(viaExplicit.IsValid);

        // 两种开关产生完全相同的启用集合……
        Assert.Equal(
            viaLegacy.Enabled.Select(p => p.Id).ToArray(),
            viaExplicit.Enabled.Select(p => p.Id).ToArray());

        // ……且都恰为基线集合多出 C07 一个 pass（按锁定链序插在 make-readonly 之后）。
        var baselineIds = baseline.Enabled.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var expected = registry.Ordered
            .Select(p => p.Id)
            .Where(id => baselineIds.Contains(id) || id == PassIds.StructValueSemantics)
            .ToArray();
        Assert.Equal(expected, viaExplicit.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void EqualityConcat_legacy_flag_and_explicit_enable_are_equivalent()
    {
        var registry = PassCatalog.BuildRegistry();
        var baselineToggles = PassChainFactory.BuildLegacyToggles(new ConvertProjectOptions());

        // convert-project: legacy property enabled (future default-on flip path).
        var viaLegacy = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildLegacyToggles(
                new ConvertProjectOptions { EqualityConcat = true }));

        // generic: --enable-pass equality-concat.
        var viaExplicit = PassPlanResolver.Resolve(
            registry,
            baselineToggles,
            enablePassIds: new[] { PassStubCatalog.Ids.EqualityConcat });

        Assert.True(viaLegacy.IsValid);
        Assert.True(viaExplicit.IsValid);
        Assert.Equal(
            viaLegacy.Enabled.Select(p => p.Id).ToArray(),
            viaExplicit.Enabled.Select(p => p.Id).ToArray());
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
                && id != PassStubCatalog.Ids.ExpressionDesugar)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            toggles.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }
}
