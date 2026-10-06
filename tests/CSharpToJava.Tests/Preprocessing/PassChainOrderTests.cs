using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Locks the registered pass sequence to the historical ConvertProject chain.
/// The fifteen hand-written blocks were replaced by the registry-driven loop, so
/// the topological sort must keep producing this exact order. Every implemented
/// pass is default-on (all-preprocessors-default-on policy) except four
/// deliberate opt-outs: C25
/// dynamic-desugar (audit-first), C26 linq-channel (observe-only
/// gate period), plus the 2026-10-06 P0 rollbacks C20 partial-merge (M3
/// member-level collapse pending) and C04 iterator-lowering (M2/M3 pending);
/// the legacy flags survive as --no-xxx negations and --disable-pass remains
/// the opt-out.
/// </summary>
public sealed class PassChainOrderTests
{
    private static readonly string[] LockedChain =
    [
        PassIds.Conditional,
        PassStubCatalog.Ids.LexicalNormalize,
        // C20 partial-merge: DEFAULT-OFF again after the 2026-10-06 P0 rollback
        // (M3 member-level pairing collapse + sdk dual-equivalence pending);
        // excluded from EnabledByDefault below.
        PassStubCatalog.Ids.PartialMerge,
        PassIds.LocalFunctionLift,
        PassIds.NullCoalesce,
        PassStubCatalog.Ids.ExpressionDesugar,
        PassIds.Enum,
        PassIds.EnumDeclaration,
        PassIds.Delegate,
        PassIds.Tuple,
        // C21 M1-M4 flipped to Implemented; default-on under the
        // all-preprocessors-default-on policy, RunAfter=tuple. Declared
        // directly after tuple so the Kahn declaration-order tiebreak pins
        // it between tuple and foreach-deconstruction (both only anchor
        // tuple).
        PassIds.AnonymousTypeLift,
        PassIds.ForeachDeconstruction,
        PassStubCatalog.Ids.ForeachSource,
        PassIds.PatternDesugar,
        PassIds.StripDefault,
        PassIds.RefOutIn,
        PassIds.Unsigned,
        PassStubCatalog.Ids.NumericBoundary,
        PassIds.Generic,
        // C09 thread-static flipped to DEFAULT-ON on 2026-10-06 after the
        // §8 gates (design §9 M2 校注). It remains physically declared between
        // generic and generic-deepen — both anchor generic only — so the Kahn
        // declaration-order tiebreak locks this
        // slot: generic -> thread-static -> generic-deepen.
        PassIds.ThreadStatic,
        PassIds.GenericDeepen,
        // C30 + C33: closed-metadata and variance-audit are both Implemented;
        // they slot in after generic-deepen (design §3.4/§4.3/§5.5).
        PassIds.ClosedMetadata,
        PassIds.VarianceAudit,
        PassIds.RecordReadOnly,
        PassIds.ArrayMethod,
        PassIds.IndexRange,
        PassIds.MakeReadonly,
        // C10 attribute-normalize flipped to DEFAULT-ON on 2026-10-06 after
        // the §8 gates (design §13). It slots immediately after make-readonly
        // (anchors generic-deepen + make-readonly, declared right after
        // make-readonly so the Kahn tiebreak pins the slot), before
        // struct-value-semantics (design §3.1/§3.2).
        PassIds.AttributeNormalize,
        PassIds.StructValueSemantics,
        PassStubCatalog.Ids.UnsafeFfm,
        PassStubCatalog.Ids.ClosureLift,
        // C04 iterator-lowering: Implemented but DEFAULT-OFF again after the
        // 2026-10-06 P0 rollback (M1 only; try-finally/using/lock lowering
        // M2/M3 pending). Stays in the locked topological sequence but is
        // excluded from EnabledByDefault below.
        PassStubCatalog.Ids.IteratorLowering,
        // C18 async-normalize is Implemented and default-on under the
        // all-preprocessors-default-on policy; anchors make-readonly +
        // closure-lift, declared right after iterator-lowering so the Kahn
        // tiebreak locks the slot iterator-lowering -> async-normalize ->
        // extension-methods -> eliminate-goto.
        PassStubCatalog.Ids.AsyncNormalize,
        // C19：extension-methods 翻转 Implemented；七锚（delegate/
        // tuple/ref-out-in/unsigned/array-method/make-readonly/iterator-lowering）
        // 保证它在 iterator-lowering 之后、eliminate-goto 之前的固定槽位。
        PassIds.ExtensionMethods,
        PassIds.EliminateGoto,
        // C32 翻转的拓扑副作用：member-desugar 的 RunAfter closure-lift 进入
        // implemented 区后，它与只依赖 tuple 的 equality-concat 在 Kahn
        // 声明序破平中先后对调（两者无相互依赖；三工程回归全绿即行为无外溢）。
        PassIds.MemberDesugar,
        // C26 M1 flips linq-channel to Implemented (default-off): anchors
        // extension-methods + anonymous-type-lift + eliminate-goto and is
        // declared directly after member-desugar, so the Kahn declaration
        // tiebreak pins the slot member-desugar -> linq-channel ->
        // concurrency-normalize.
        PassStubCatalog.Ids.LinqChannel,
        // C29 翻转：描述符置于 member-desugar 之后，
        // 与 equality-concat 互无 RunAfter 约束，相对次序由注册表声明序锁定。
        PassStubCatalog.Ids.ConcurrencyNormalize,
        PassStubCatalog.Ids.EqualityConcat,
        // C42 platform-isolation is Implemented; it anchors eliminate-goto and
        // stays before java-keywords (§3.3).
        PassStubCatalog.Ids.PlatformIsolation,
        // C25 dynamic-desugar is Implemented but default-OFF long-term
        // (design §3.2/§8), so it is in the locked topological sequence but
        // excluded from EnabledByDefault below. Declared right after
        // platform-isolation so the Kahn tiebreak locks the tail slot
        // platform-isolation -> dynamic-desugar -> java-keywords.
        PassStubCatalog.Ids.DynamicDesugar,
        PassIds.JavaKeywords,
    ];

    // All-preprocessors-default-on policy: every implemented pass is
    // default-enabled; --disable-pass / legacy --no-xxx flags opt out.
    // Default-on policy exceptions — four deliberate opt-outs:
    // C25 dynamic-desugar (audit-first, §8 corpus gate), C26 linq-channel
    // (observe-only gate period), and the 2026-10-06 P0 rollbacks C20
    // partial-merge (M3 member-level collapse + profile pending) and C04
    // iterator-lowering (M2/M3 pending).
    private static readonly string[] EnabledByDefault = LockedChain
        .Where(id => id != PassStubCatalog.Ids.DynamicDesugar
            && id != PassStubCatalog.Ids.LinqChannel
            && id != PassStubCatalog.Ids.PartialMerge
            && id != PassStubCatalog.Ids.IteratorLowering)
        .ToArray();

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

        // Stubs never execute; the naming pass is the tail of the executable
        // Implemented chain and of the resolved default plan. Until the C25
        // flip the dynamic-desugar stub's late declaration position also
        // pinned java-keywords after the residual linq-channel stub in the
        // full Ordered list; with dynamic Implemented (declared before the
        // Stubs block), the Kahn declaration-order tiebreak lets
        // java-keywords precede not-anchored stubs — the executed-plan
        // guarantee below is the contract that matters.
        Assert.Equal(
            PassIds.JavaKeywords,
            registry.Ordered.Last(pass => pass.Status == PassStatus.Implemented).Id);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        Assert.Equal(PassIds.JavaKeywords, plan.Enabled[^1].Id);
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
    public void AttributeNormalize_slots_after_make_readonly_precedes_member_desugar_and_is_default_on()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub row is gone and the single descriptor is Implemented.
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassIds.AttributeNormalize && pass.Status == PassStatus.Stub);
        var descriptor = PassCatalog.ImplementedById(PassIds.AttributeNormalize);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);

        var ordered = registry.Ordered
            .Where(pass => pass.Status == PassStatus.Implemented).ToList();
        var genericDeepen = ordered.FindIndex(pass => pass.Id == PassIds.GenericDeepen);
        var makeReadonly = ordered.FindIndex(pass => pass.Id == PassIds.MakeReadonly);
        var attributeNormalize = ordered.FindIndex(pass => pass.Id == PassIds.AttributeNormalize);
        var memberDesugar = ordered.FindIndex(pass => pass.Id == PassIds.MemberDesugar);
        var javaKeywords = ordered.FindIndex(pass => pass.Id == PassIds.JavaKeywords);

        Assert.True(genericDeepen >= 0 && makeReadonly >= 0
            && attributeNormalize >= 0 && memberDesugar >= 0 && javaKeywords >= 0);
        Assert.True(genericDeepen < attributeNormalize,
            "attribute-normalize must run after generic-deepen");
        Assert.True(makeReadonly < attributeNormalize,
            "attribute-normalize must run after make-readonly");
        Assert.True(attributeNormalize < memberDesugar,
            "attribute-normalize must precede member-desugar");
        Assert.True(attributeNormalize < javaKeywords,
            "attribute-normalize must precede java-keywords");

        // Default plan runs the pass (flipped 2026-10-06 after the §8 gates).
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(
            baseline.Enabled, pass => pass.Id == PassIds.AttributeNormalize);

        // --disable-pass opts out while the rest of the locked chain stays.
        var disabled = PassPlanResolver.Resolve(
            registry, disablePassIds: new[] { PassIds.AttributeNormalize });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(
            disabled.Enabled, pass => pass.Id == PassIds.AttributeNormalize);

        // Enabling and disabling the same id in one request is a CS2J9002 conflict.
        var conflict = PassPlanResolver.Resolve(
            registry,
            enablePassIds: new[] { PassIds.AttributeNormalize },
            disablePassIds: new[] { PassIds.AttributeNormalize });
        Assert.False(conflict.IsValid);
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

        // Default-on (all-on policy): present in the default plan with every
        // legacy toggle at its baseline.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.Contains(baseline.Enabled, p => p.Id == PassStubCatalog.Ids.ClosureLift);

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
    public void AsyncNormalize_is_Implemented_default_on_and_slots_between_iterator_and_eliminate_goto()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub entry is gone; the id lives in the Implemented catalog only.
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassStubCatalog.Ids.AsyncNormalize && pass.Status == PassStatus.Stub);

        var descriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.AsyncNormalize);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal("C18, C38 (C44 subtopic)", descriptor.CatalogItems);
        Assert.Equal(".cs2j-async-normalize-src", descriptor.IntermediateDirectoryName);

        // Default-on: present in the default plan.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(
            baseline.Enabled, pass => pass.Id == PassStubCatalog.Ids.AsyncNormalize);

        // Explicit disable removes it from the plan while keeping every other
        // default-on pass intact.
        var disabled = PassPlanResolver.Resolve(
            registry,
            disablePassIds: new[] { PassStubCatalog.Ids.AsyncNormalize });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(
            disabled.Enabled, pass => pass.Id == PassStubCatalog.Ids.AsyncNormalize);

        // Explicit opt-in with every other toggle off: the enabled order must
        // be iterator-lowering -> async-normalize -> eliminate-goto (anchors +
        // declaration-order tiebreak; no edge between async-normalize and goto).
        var allOff = PassChainFactory.BuildAllDisabledToggles(registry);
        var plan = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[]
            {
                PassStubCatalog.Ids.IteratorLowering,
                PassStubCatalog.Ids.AsyncNormalize,
                PassIds.EliminateGoto,
            });

        Assert.True(plan.IsValid);
        Assert.Equal(new[]
        {
            PassStubCatalog.Ids.IteratorLowering,
            PassStubCatalog.Ids.AsyncNormalize,
            PassIds.EliminateGoto,
        }, plan.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void PlatformIsolation_is_default_on_in_the_default_plan_and_not_a_stub()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub entry is gone; the id lives in the Implemented catalog only.
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassStubCatalog.Ids.PlatformIsolation && pass.Status == PassStatus.Stub);

        var descriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.PlatformIsolation);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        Assert.Contains(plan.Enabled, pass => pass.Id == PassStubCatalog.Ids.PlatformIsolation);
    }

    [Fact]
    public void PlatformIsolation_when_enabled_runs_after_eliminate_goto_before_java_keywords()
    {
        var registry = PassCatalog.BuildRegistry();
        var allOff = PassChainFactory.BuildAllDisabledToggles(registry);

        var plan = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[]
            {
                PassIds.EliminateGoto,
                PassStubCatalog.Ids.PlatformIsolation,
                PassIds.JavaKeywords,
            });

        Assert.True(plan.IsValid);
        Assert.Equal(
            new[]
            {
                PassIds.EliminateGoto,
                PassStubCatalog.Ids.PlatformIsolation,
                PassIds.JavaKeywords,
            },
            plan.Enabled.Select(pass => pass.Id).ToArray());
    }

    [Fact]
    public void ConcurrencyNormalize_precedes_platform_isolation_and_dynamic_sits_between_platform_and_java_keywords()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered.ToList();

        var concurrency = ordered.FindIndex(pass => pass.Id == PassStubCatalog.Ids.ConcurrencyNormalize);
        var platform = ordered.FindIndex(pass => pass.Id == PassStubCatalog.Ids.PlatformIsolation);
        var javaKeywords = ordered.FindIndex(pass => pass.Id == PassIds.JavaKeywords);
        var dynamic = ordered.FindIndex(pass => pass.Id == PassStubCatalog.Ids.DynamicDesugar);

        Assert.True(concurrency >= 0 && platform >= 0 && javaKeywords >= 0 && dynamic >= 0);
        Assert.True(concurrency < platform, "concurrency-normalize must precede platform-isolation (locked chain)");
        // C25 flip: dynamic-desugar used to be the trailing stub; as an
        // Implemented pass its Kahn declaration slot is platform-isolation ->
        // dynamic-desugar -> java-keywords (design §3.3).
        Assert.True(platform < dynamic, "platform-isolation must precede dynamic-desugar (locked chain)");
        Assert.True(dynamic < javaKeywords, "dynamic-desugar must precede java-keywords");
    }

    [Fact]
    public void DynamicDesugar_is_Implemented_default_off_and_not_a_stub()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub row is gone; the id resolves to the single Implemented
        // descriptor (the CS2J9001 stub-enable path no longer applies).
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassStubCatalog.Ids.DynamicDesugar && pass.Status == PassStatus.Stub);
        Assert.Single(registry.Ordered, pass => pass.Id == PassStubCatalog.Ids.DynamicDesugar);

        var descriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.DynamicDesugar);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.False(descriptor.DefaultEnabled);
        Assert.Equal(".cs2j-dynamic-desugar-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Abi, descriptor.Stage);
        Assert.Equal(new[] { PassIds.EliminateGoto }, descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
        Assert.Equal("C25", descriptor.CatalogItems);

        // Default-off: absent from the default plan.
        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        Assert.DoesNotContain(
            plan.Enabled, pass => pass.Id == PassStubCatalog.Ids.DynamicDesugar);
    }

    [Fact]
    public void DynamicDesugar_when_enabled_runs_after_eliminate_goto_before_java_keywords()
    {
        var registry = PassCatalog.BuildRegistry();
        var allOff = PassChainFactory.BuildAllDisabledToggles(registry);

        var plan = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[]
            {
                PassIds.EliminateGoto,
                PassStubCatalog.Ids.DynamicDesugar,
                PassIds.JavaKeywords,
            });

        Assert.True(plan.IsValid);
        Assert.Equal(
            new[]
            {
                PassIds.EliminateGoto,
                PassStubCatalog.Ids.DynamicDesugar,
                PassIds.JavaKeywords,
            },
            plan.Enabled.Select(pass => pass.Id).ToArray());
    }

    [Fact]
    public void DynamicDesugar_alone_plans_as_Implemented_and_never_CS2J9001()
    {
        var registry = PassCatalog.BuildRegistry();

        // Explicit opt-in against the all-disabled map: the resolver must
        // accept it as Implemented (no CS2J9001 stub rejection), exactly like
        // the platform-isolation/anonymous-type-lift precedent.
        var plan = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildAllDisabledToggles(registry),
            enablePassIds: new[] { PassStubCatalog.Ids.DynamicDesugar });

        Assert.True(plan.IsValid);
        Assert.Empty(plan.Errors);
        var enabled = Assert.Single(plan.Enabled);
        Assert.Equal(PassStubCatalog.Ids.DynamicDesugar, enabled.Id);
        Assert.Equal(PassStatus.Implemented, enabled.Status);
    }

    [Fact]
    public void ListPasses_shows_dynamic_desugar_implemented_default_off_with_C25_rule()
    {
        var lines = PassListFormatter.BuildLines(PassCatalog.BuildRegistry());

        var line = Assert.Single(lines, row =>
            row.TrimStart().StartsWith(
                PassStubCatalog.Ids.DynamicDesugar, StringComparison.Ordinal));
        Assert.Contains("Implemented", line);
        Assert.Contains("off", line);
        Assert.DoesNotContain(" on ", line);
        Assert.Contains(PassIds.EliminateGoto, line);
        Assert.Contains("rules=C25", line);
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
        Assert.Equal("C33", byId[PassIds.VarianceAudit].CatalogItems);
        Assert.True(byId[PassIds.VarianceAudit].DefaultEnabled);
        Assert.Equal("C03, C37", byId[PassIds.PatternDesugar].CatalogItems);
        Assert.Equal("C04", byId[PassStubCatalog.Ids.IteratorLowering].CatalogItems);
        // P0 rollback (2026-10-06): default-off until M2/M3 land.
        Assert.False(byId[PassStubCatalog.Ids.IteratorLowering].DefaultEnabled);
        Assert.Equal("C15, C34, C17, C22", byId[PassStubCatalog.Ids.NumericBoundary].CatalogItems);
        Assert.Equal("C07", byId[PassIds.StructValueSemantics].CatalogItems);
        Assert.Equal("C05", byId[PassIds.EnumDeclaration].CatalogItems);
        Assert.Equal("C06", byId[PassStubCatalog.Ids.UnsafeFfm].CatalogItems);
        Assert.Equal("C11, C12, C13, C14, C39, C46, C50, C27", byId[PassIds.MemberDesugar].CatalogItems);
        Assert.Equal("C16, C40", byId[PassStubCatalog.Ids.ForeachSource].CatalogItems);
        Assert.True(byId[PassStubCatalog.Ids.ForeachSource].DefaultEnabled);
        Assert.Equal("C47", byId[PassStubCatalog.Ids.EqualityConcat].CatalogItems);
        Assert.Equal("C23, C24, C35, C36, C51", byId[PassStubCatalog.Ids.ExpressionDesugar].CatalogItems);
        Assert.Equal("C30", byId[PassIds.ClosedMetadata].CatalogItems);
        Assert.Equal("C20", byId[PassStubCatalog.Ids.PartialMerge].CatalogItems);
        // P0 rollback (2026-10-06): default-off pending M3 pairing collapse.
        Assert.False(byId[PassStubCatalog.Ids.PartialMerge].DefaultEnabled);
        Assert.Equal("C32, C45", byId[PassStubCatalog.Ids.ClosureLift].CatalogItems);
        Assert.True(byId[PassStubCatalog.Ids.ClosureLift].DefaultEnabled);
        Assert.Equal("C29", byId[PassStubCatalog.Ids.ConcurrencyNormalize].CatalogItems);
        Assert.True(byId[PassStubCatalog.Ids.ConcurrencyNormalize].DefaultEnabled);
        Assert.Equal(".cs2j-concurrency-normalize-src", byId[PassStubCatalog.Ids.ConcurrencyNormalize].IntermediateDirectoryName);
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
            VarianceAuditOptionsFactory = () => new CSharpToJava.Core.VarianceAudit.VarianceAuditOptions(),
            EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            LinqChannelOptionsFactory = () => new LinqChannelOptions(),
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
        // C36 expression-desugar, C29 concurrency-normalize, C42 platform-isolation,
        // C25 dynamic-desugar, C30 closed-metadata, C21 anonymous-type-lift,
        // C18 async-normalize/default-on, C26 linq-channel/default-off,
        // C20 partial-merge/default-off, C04 iterator-lowering/default-off,
        // C09 thread-static/default-on (flipped 2026-10-06; still no legacy
        // flag), C10 attribute-normalize/default-on (flipped 2026-10-06;
        // still no legacy flag))
        // are reachable solely through their descriptor default plus --enable-pass/--pass
        // (C04 additionally carries a legacy toggle whose default follows the descriptor).
        Assert.Equal(
            LockedChain.Where(id => id != PassIds.GenericDeepen && id != PassIds.PatternDesugar
                && id != PassIds.EnumDeclaration
                && id != PassIds.ThreadStatic
                && id != PassStubCatalog.Ids.UnsafeFfm
                && id != PassStubCatalog.Ids.ExpressionDesugar
                && id != PassStubCatalog.Ids.LexicalNormalize
                && id != PassStubCatalog.Ids.PartialMerge
                && id != PassStubCatalog.Ids.ClosureLift
                && id != PassIds.ExtensionMethods
                && id != PassStubCatalog.Ids.ConcurrencyNormalize
                && id != PassIds.VarianceAudit
                && id != PassStubCatalog.Ids.PlatformIsolation
                && id != PassStubCatalog.Ids.DynamicDesugar
                && id != PassIds.ClosedMetadata
                && id != PassIds.AnonymousTypeLift
                && id != PassStubCatalog.Ids.AsyncNormalize
                && id != PassIds.AttributeNormalize
                && id != PassStubCatalog.Ids.LinqChannel)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            toggles.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ConcurrencyNormalize_is_default_on_and_stays_absent_from_the_legacy_toggle_map()
    {
        var registry = PassCatalog.BuildRegistry();

        // Default-on (all-on policy): present in the default plan and still
        // absent from the legacy-toggle map (registry-default only pass).
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.Contains(baseline.Enabled, p => p.Id == PassStubCatalog.Ids.ConcurrencyNormalize);

        // Explicit disable removes it; with every legacy toggle off it runs
        // alone (its eliminate-goto anchor stays disabled), which pins its
        // resolver slot.
        var allOff = LockedChain.ToDictionary(id => id, _ => false);
        var isolated = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[] { PassStubCatalog.Ids.ConcurrencyNormalize });

        Assert.True(isolated.IsValid);
        Assert.Equal(
            new[] { PassStubCatalog.Ids.ConcurrencyNormalize },
            isolated.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void AnonymousTypeLift_is_implemented_default_on_with_tuple_anchor_and_no_stub_remains()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub row is gone and the id resolves to the single Implemented
        // descriptor (the CS2J9001 stub-enable path no longer applies to it).
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassIds.AnonymousTypeLift && pass.Status == PassStatus.Stub);
        Assert.Single(registry.Ordered.Where(pass => pass.Id == PassIds.AnonymousTypeLift));

        var descriptor = PassCatalog.ImplementedById(PassIds.AnonymousTypeLift);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(".cs2j-anonymous-type-lift-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Declarative, descriptor.Stage);
        Assert.Equal(new[] { PassIds.Tuple }, descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
        Assert.Equal("C21", descriptor.CatalogItems);

        // Default-on: present in the default plan.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(baseline.Enabled, pass => pass.Id == PassIds.AnonymousTypeLift);

        // Explicit disable removes it; with every legacy toggle off it can
        // still be re-enabled explicitly and enters the plan as Implemented
        // (never CS2J9001).
        var disabled = PassPlanResolver.Resolve(
            registry,
            disablePassIds: new[] { PassIds.AnonymousTypeLift });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(disabled.Enabled, pass => pass.Id == PassIds.AnonymousTypeLift);

        var enabled = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildAllDisabledToggles(registry),
            enablePassIds: new[] { PassIds.AnonymousTypeLift });
        Assert.True(enabled.IsValid);
        var entry = Assert.Single(enabled.Enabled);
        Assert.Equal(PassIds.AnonymousTypeLift, entry.Id);
        Assert.Equal(PassStatus.Implemented, entry.Status);
    }

    [Fact]
    public void AnonymousTypeLift_enabled_sorts_between_tuple_and_foreach_deconstruction()
    {
        var registry = PassCatalog.BuildRegistry();
        var allOff = PassChainFactory.BuildAllDisabledToggles(registry);

        var plan = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[]
            {
                PassIds.Tuple,
                PassIds.AnonymousTypeLift,
                PassIds.ForeachDeconstruction,
            });

        Assert.True(plan.IsValid);
        Assert.Equal(
            new[]
            {
                PassIds.Tuple,
                PassIds.AnonymousTypeLift,
                PassIds.ForeachDeconstruction,
            },
            plan.Enabled.Select(pass => pass.Id).ToArray());
    }

    [Fact]
    public void AnonymousTypeLift_can_run_alone_when_explicitly_enabled()
    {
        var registry = PassCatalog.BuildRegistry();

        var plan = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildAllDisabledToggles(registry),
            enablePassIds: new[] { PassIds.AnonymousTypeLift });

        Assert.True(plan.IsValid);
        Assert.Equal(
            new[] { PassIds.AnonymousTypeLift },
            plan.Enabled.Select(pass => pass.Id).ToArray());
    }

    [Fact]
    public void ThreadStatic_is_default_on_in_the_default_plan_but_has_no_legacy_toggle()
    {
        var registry = PassCatalog.BuildRegistry();

        // Default-on since the 2026-10-06 §8 gate flip (design §9 M2 校注):
        // the baseline plan includes it at its locked slot.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(baseline.Enabled, pass => pass.Id == PassIds.ThreadStatic);

        // No convert-project legacy toggle exists for C09.
        var toggles = PassChainFactory.BuildLegacyToggles(new ConvertProjectOptions());
        Assert.DoesNotContain(toggles.Keys, id => id == PassIds.ThreadStatic);

        // Explicit disable against the baseline stays valid and omits it.
        var disabled = PassPlanResolver.Resolve(
            registry,
            disablePassIds: new[] { PassIds.ThreadStatic });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(disabled.Enabled, pass => pass.Id == PassIds.ThreadStatic);
    }

    [Fact]
    public void ThreadStatic_when_enabled_runs_in_its_locked_slot_between_generic_and_generic_deepen()
    {
        var registry = PassCatalog.BuildRegistry();
        var allOff = LockedChain.ToDictionary(id => id, _ => false);

        // Isolated enable: with every other pass off, thread-static alone
        // enters the plan as Implemented (its generic anchor stays disabled;
        // the resolver performs no dependency closure).
        var alone = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[] { PassIds.ThreadStatic });

        Assert.True(alone.IsValid);
        Assert.Equal(
            new[] { PassIds.ThreadStatic },
            alone.Enabled.Select(pass => pass.Id).ToArray());
        Assert.Equal(PassStatus.Implemented, Assert.Single(alone.Enabled).Status);

        // Slot lock: generic -> thread-static -> generic-deepen (all three
        // anchor generic; declaration order breaks the tie per design §3.1).
        var slotted = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[]
            {
                PassIds.Generic,
                PassIds.ThreadStatic,
                PassIds.GenericDeepen,
            });

        Assert.True(slotted.IsValid);
        Assert.Equal(
            new[]
            {
                PassIds.Generic,
                PassIds.ThreadStatic,
                PassIds.GenericDeepen,
            },
            slotted.Enabled.Select(pass => pass.Id).ToArray());
    }

    [Fact]
    public void LinqChannel_is_implemented_default_off_with_three_anchors_and_no_stub_remains()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub row is gone and the id resolves to the single Implemented
        // descriptor (the CS2J9001 stub-enable path no longer applies).
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassStubCatalog.Ids.LinqChannel && pass.Status == PassStatus.Stub);
        Assert.Single(registry.Ordered.Where(pass => pass.Id == PassStubCatalog.Ids.LinqChannel));

        var descriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.LinqChannel);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.False(descriptor.DefaultEnabled);
        Assert.Equal(".cs2j-linq-channel-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.ControlFlow, descriptor.Stage);
        Assert.Equal(
            new[]
            {
                PassIds.ExtensionMethods,
                PassIds.AnonymousTypeLift,
                PassIds.EliminateGoto,
            },
            descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
        Assert.Equal("C26", descriptor.CatalogItems);

        // Default-off: absent from the default plan.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.DoesNotContain(baseline.Enabled, pass => pass.Id == PassStubCatalog.Ids.LinqChannel);

        // Explicit opt-in with every toggle off plans it as Implemented
        // (never CS2J9001).
        var enabled = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildAllDisabledToggles(registry),
            enablePassIds: new[] { PassStubCatalog.Ids.LinqChannel });
        Assert.True(enabled.IsValid);
        var entry = Assert.Single(enabled.Enabled);
        Assert.Equal(PassStubCatalog.Ids.LinqChannel, entry.Id);
        Assert.Equal(PassStatus.Implemented, entry.Status);

        // The concrete adapter exists for the flipped descriptor.
        var adapter = PassChainFactory.CreateAdapter(
            PassStubCatalog.Ids.LinqChannel,
            new PassChainConfiguration
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
                LinqChannelOptionsFactory = () => new LinqChannelOptions(),
            });
        Assert.Equal(PassStubCatalog.Ids.LinqChannel, adapter.Descriptor.Id);
        Assert.True(adapter.VerifyBuild);
        Assert.True(adapter.VerifyTests);
        Assert.True(adapter.DeleteBuildOutputsAfterRestore);
    }

    [Fact]
    public void LinqChannel_enabled_sorts_between_member_desugar_and_concurrency_normalize()
    {
        var registry = PassCatalog.BuildRegistry();
        var allOff = PassChainFactory.BuildAllDisabledToggles(registry);

        // member-desugar and concurrency-normalize are the declaration-order
        // neighbors; the locked slot is member-desugar -> linq-channel ->
        // concurrency-normalize (linq-channel anchors eliminate-goto, which
        // stays disabled in this isolated selection, so declaration order
        // breaks the tie exactly as in the full locked chain).
        var plan = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[]
            {
                PassIds.MemberDesugar,
                PassStubCatalog.Ids.LinqChannel,
                PassStubCatalog.Ids.ConcurrencyNormalize,
            });

        Assert.True(plan.IsValid);
        Assert.Equal(
            new[]
            {
                PassIds.MemberDesugar,
                PassStubCatalog.Ids.LinqChannel,
                PassStubCatalog.Ids.ConcurrencyNormalize,
            },
            plan.Enabled.Select(pass => pass.Id).ToArray());
    }
}
