using CSharpToJava.CLI;
using CommandLine;
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
/// pass is default-on (all-preprocessors-default-on policy), including
/// C20 partial-merge and C04 iterator-lowering (both flipped 2026-10-11);
/// the legacy flags survive as --no-xxx negations and --disable-pass remains
/// the opt-out. C26 linq-channel joined the default chain on 2026-10-06
/// after the M4 §8 gates (--no-linq-rewrite maps to --disable-pass
/// linq-channel). C25 dynamic-desugar AUDIT joined the default chain on
/// 2026-10-06 (Rewrite stays opt-in via --dynamic-mode Rewrite).
/// </summary>
public sealed class PassChainOrderTests
{
    private static readonly string[] LockedChain =
    [
        PassIds.Conditional,
        PassIds.LexicalNormalize,
        // C20 partial-merge flipped to DEFAULT-ON on 2026-10-11 under the
        // all-preprocessors-default-on policy.
        PassIds.PartialMerge,
        PassIds.LocalFunctionLift,
        PassIds.NullCoalesce,
        PassIds.ExpressionDesugar,
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
        PassIds.ForeachSource,
        PassIds.PatternDesugar,
        PassIds.StripDefault,
        PassIds.RefOutIn,
        // ABI post-pass over ref-out-in's residual MemoryMarshal.GetReference
        // ref-local + Unsafe.ReadUnaligned/Unsafe.Add shape. RunAfter=ref-out-in
        // and declared directly after it, so the Kahn declaration-order
        // tiebreak pins ref-out-in -> unsafe-ref-lowering -> unsigned.
        PassIds.UnsafeRefLowering,
        // Control-flow post-pass over ref-out-in's residual `using (... ref/out ...)`
        // resource expressions. RunAfter=ref-out-in and physically declared right
        // after unsafe-ref-lowering, so the Kahn declaration-order tiebreak pins
        // the slot unsafe-ref-lowering -> using-resource-lift -> unsigned.
        PassIds.UsingResourceLift,
        // ABI pre-pass over span-backed fixed statements. RunAfter ref-out-in +
        // using-resource-lift and declared directly before unsigned, so the Kahn
        // tiebreak pins using-resource-lift -> fixed-span-to-array -> unsigned.
        // It must stay before unsafe-ffm so R3 can consume fixed over array.
        PassIds.FixedSpanToArray,
        // Expression pre-pass over the code-registered known-method table. It
        // is declared right after fixed-span-to-array and anchors it, so the
        // Kahn tiebreak pins fixed-span-to-array -> compat-method-replacement ->
        // pointer-diff-inline -> unsigned. It must stay before unsafe-ffm so the
        // replaced transcoder body carries no fixed/pointer code.
        PassIds.CompatMethodReplacement,
        // Expression pre-pass over ref-out-in's output. It inlines file-local
        // same-type pointer-difference helpers before unsafe-ffm so the helper
        // pointer arguments no longer escape (UF020). RunAfter ref-out-in and
        // physically declared right after compat-method-replacement, so the Kahn
        // declaration-order tiebreak pins compat-method-replacement ->
        // pointer-diff-inline -> unsigned.
        PassIds.PointerDiffInline,
        PassIds.Unsigned,
        PassIds.NumericBoundary,
        PassIds.Generic,
        // R2/CN (upgrade plan §6.5): generic-constraint is physically declared
        // between generic and thread-static; thread-static and generic-deepen
        // both anchor generic-constraint, so the Kahn declaration-order
        // tiebreak locks generic -> generic-constraint -> thread-static ->
        // generic-deepen.
        PassIds.GenericConstraint,
        // C09 thread-static flipped to DEFAULT-ON on 2026-10-06 after the
        // §8 gates (design §9 M2 校注). It remains physically declared between
        // generic and generic-deepen — both anchor generic only — so the Kahn
        // declaration-order tiebreak locks this slot.
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
        PassIds.UnsafeFfm,
        // M2: stackalloc-lowering is an independent read-only post-pass over
        // unsafe-ffm residuals. RunAfter=unsafe-ffm and physically declared
        // right after it, so the Kahn declaration-order tiebreak pins the slot
        // unsafe-ffm -> stackalloc-lowering -> ref-struct-lowering -> closure-lift.
        // Default-on is safe: it only rewrites constructs Java can never accept.
        PassIds.StackAllocLowering,
        // M2: ref-struct-lowering is an independent read-only post-pass over
        // stackalloc-lowering's output. RunAfter=stackalloc-lowering and
        // physically declared right after it, so the Kahn declaration-order
        // tiebreak pins the slot stackalloc-lowering -> ref-struct-lowering ->
        // closure-lift. Default-on is safe: it only removes the `ref` modifier
        // from structs without any ref-only member.
        PassIds.RefStructLowering,
        // M4: ref-struct-span-field-memory-lift is an independent read-only
        // post-pass over ref-struct-lowering's output. RunAfter=ref-struct-lowering
        // and physically declared right after it, so the Kahn declaration-order
        // tiebreak pins the slot ref-struct-lowering -> ref-struct-span-field-
        // memory-lift -> pointer-deref-index -> closure-lift.
        PassIds.RefStructSpanFieldMemoryLift,
        // M3: pointer-deref-index is an independent read-only expression
        // post-pass over unsafe-ffm's preserved pointer dereferences. It
        // anchors unsafe-ffm and is physically declared right after
        // ref-struct-span-field-memory-lift, so the Kahn declaration-order
        // tiebreak pins the slot ref-struct-span-field-memory-lift ->
        // pointer-deref-index -> closure-lift.
        PassIds.PointerDerefIndex,
        PassIds.ClosureLift,
        // C04 iterator-lowering flipped to DEFAULT-ON on 2026-10-11 under the
        // all-preprocessors-default-on policy.
        PassIds.IteratorLowering,
        // C18 async-normalize is Implemented and default-on under the
        // all-preprocessors-default-on policy; anchors make-readonly +
        // closure-lift, declared right after iterator-lowering so the Kahn
        // tiebreak locks the slot iterator-lowering -> async-normalize ->
        // extension-methods -> eliminate-goto.
        PassIds.AsyncNormalize,
        // C19：extension-methods 翻转 Implemented；七锚（delegate/
        // tuple/ref-out-in/unsigned/array-method/make-readonly/iterator-lowering）
        // 保证它在 iterator-lowering 之后、eliminate-goto 之前的固定槽位。
        PassIds.ExtensionMethods,
        PassIds.EliminateGoto,
        // C32 翻转的拓扑副作用：member-desugar 的 RunAfter closure-lift 进入
        // implemented 区后，它与只依赖 tuple 的 equality-concat 在 Kahn
        // 声明序破平中先后对调（两者无相互依赖；三工程回归全绿即行为无外溢）。
        PassIds.MemberDesugar,
        // C26 linq-channel: Implemented and flipped default-on at M4
        // (2026-10-06, after the §8 gates). Anchors extension-methods +
        // anonymous-type-lift + eliminate-goto and is declared directly after
        // member-desugar, so the Kahn declaration tiebreak pins the slot
        // member-desugar -> linq-channel -> concurrency-normalize.
        PassIds.LinqChannel,
        // C29 翻转：描述符置于 member-desugar 之后，
        // 与 equality-concat 互无 RunAfter 约束，相对次序由注册表声明序锁定。
        PassIds.ConcurrencyNormalize,
        PassIds.EqualityConcat,
        // C33 R4: chain-tail Expression pass fed by the variance-audit
        // inventory/classifier. Anchors member-desugar + linq-channel +
        // equality-concat and is declared right after equality-concat, so the
        // Kahn declaration-order tiebreak pins the slot
        // equality-concat -> variance-use-site-normalize -> platform-isolation.
        PassIds.VarianceUseSiteNormalize,
        // C42 platform-isolation is Implemented; it anchors eliminate-goto and
        // stays before java-keywords (§3.3). M3 adds a RunAfter edge on
        // unsafe-ffm (already locked earlier at slot 77): the edge is consistent
        // with this sequence, so the Kahn result is unchanged and the cycle
        // guard below still proves acyclicity (附录裁决2).
        PassIds.PlatformIsolation,
        // C25 dynamic-desugar AUDIT flipped to DEFAULT-ON on 2026-10-06
        // (design §8 audit gates); Rewrite stays opt-in. Declared right after
        // platform-isolation so the Kahn tiebreak locks the tail slot
        // platform-isolation -> dynamic-desugar -> java-keywords.
        PassIds.DynamicDesugar,
        PassIds.JavaKeywords,
        // M0-b: normalization-audit is the final read-only gate. It stays
        // default-off (enabled only through --profile java-ready|audit or an
        // explicit --enable-pass) until the residual corpus reaches zero.
        PassIds.NormalizationAudit,
    ];

    // All-preprocessors-default-on policy: every implemented pass is
    // default-enabled except normalization-audit (the trailing opt-in
    // read-only gate); --disable-pass / legacy --no-xxx flags opt out.
    private static readonly string[] EnabledByDefault = LockedChain[..^1];

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
                < ordered.FindIndex(p => p.Id == PassIds.LexicalNormalize)
                && ordered.FindIndex(p => p.Id == PassIds.LexicalNormalize)
                < ordered.FindIndex(p => p.Id == PassIds.PartialMerge)
                && ordered.FindIndex(p => p.Id == PassIds.PartialMerge)
                < ordered.FindIndex(p => p.Id == PassIds.LocalFunctionLift),
            "expected conditional < lexical-normalize < partial-merge < local-function-lift");

        Assert.True(
            ordered.FindIndex(p => p.Id == PassIds.PartialMerge)
                < ordered.FindIndex(p => p.Id == PassIds.MemberDesugar),
            "expected partial-merge < member-desugar (catalog: C46 after C20)");
    }

    [Fact]
    public void NormalizationAudit_is_the_last_implemented_pass_but_java_keywords_stays_last_by_default()
    {
        var registry = PassCatalog.BuildRegistry();

        // Stubs never execute; normalization-audit is the tail of the
        // executable Implemented chain. The default plan (audit opt-in only)
        // still ends at java-keywords.
        Assert.Equal(
            PassIds.NormalizationAudit,
            registry.Ordered.Last(pass => pass.Status == PassStatus.Implemented).Id);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        Assert.DoesNotContain(plan.Enabled, pass => pass.Id == PassIds.NormalizationAudit);
        Assert.Equal(PassIds.JavaKeywords, plan.Enabled[^1].Id);

        // Explicitly enabled audit sorts after java-keywords.
        var auditPlan = PassPlanResolver.Resolve(
            registry, enablePassIds: new[] { PassIds.NormalizationAudit });
        Assert.True(auditPlan.IsValid);
        Assert.Equal(PassIds.NormalizationAudit, auditPlan.Enabled[^1].Id);
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
            plan.Enabled, p => p.Id == PassIds.UnsafeFfm);
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
            [PassIds.IteratorLowering] = true,
        };
        var plan = PassPlanResolver.Resolve(
            registry,
            isolatedToggles,
            enablePassIds: new[] { PassIds.UnsafeFfm });

        Assert.True(plan.IsValid);
        Assert.Equal(new[]
        {
            PassIds.MakeReadonly,
            PassIds.UnsafeFfm,
            PassIds.IteratorLowering,
        }, plan.Enabled.Select(p => p.Id).ToArray());

        // When the anchor is co-enabled, the adjacent slot is
        // struct-value-semantics → unsafe-ffm.
        var withAnchor = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[]
            {
                PassIds.StructValueSemantics,
                PassIds.UnsafeFfm,
            });

        Assert.True(withAnchor.IsValid);
        Assert.Equal(new[]
        {
            PassIds.StructValueSemantics,
            PassIds.UnsafeFfm,
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
        Assert.Contains(baseline.Enabled, p => p.Id == PassIds.ClosureLift);

        // Isolated slot: ref-out-in → closure-lift → iterator-lowering.
        var isolatedToggles = new Dictionary<string, bool>(allOff)
        {
            [PassIds.RefOutIn] = true,
            [PassIds.IteratorLowering] = true,
        };
        var plan = PassPlanResolver.Resolve(
            registry,
            isolatedToggles,
            enablePassIds: new[] { PassIds.ClosureLift });

        Assert.True(plan.IsValid);
        Assert.Equal(new[]
        {
            PassIds.RefOutIn,
            PassIds.ClosureLift,
            PassIds.IteratorLowering,
        }, plan.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void AsyncNormalize_is_Implemented_default_on_and_slots_between_iterator_and_eliminate_goto()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub entry is gone; the id lives in the Implemented catalog only.
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassIds.AsyncNormalize && pass.Status == PassStatus.Stub);

        var descriptor = PassCatalog.ImplementedById(PassIds.AsyncNormalize);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal("C18, C38 (C44 subtopic)", descriptor.CatalogItems);
        Assert.Equal(".cs2j-async-normalize-src", descriptor.IntermediateDirectoryName);

        // Default-on: present in the default plan.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(
            baseline.Enabled, pass => pass.Id == PassIds.AsyncNormalize);

        // Explicit disable removes it from the plan while keeping every other
        // default-on pass intact.
        var disabled = PassPlanResolver.Resolve(
            registry,
            disablePassIds: new[] { PassIds.AsyncNormalize });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(
            disabled.Enabled, pass => pass.Id == PassIds.AsyncNormalize);

        // Explicit opt-in with every other toggle off: the enabled order must
        // be iterator-lowering -> async-normalize -> eliminate-goto (anchors +
        // declaration-order tiebreak; no edge between async-normalize and goto).
        var allOff = PassChainFactory.BuildAllDisabledToggles(registry);
        var plan = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[]
            {
                PassIds.IteratorLowering,
                PassIds.AsyncNormalize,
                PassIds.EliminateGoto,
            });

        Assert.True(plan.IsValid);
        Assert.Equal(new[]
        {
            PassIds.IteratorLowering,
            PassIds.AsyncNormalize,
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
            pass => pass.Id == PassIds.PlatformIsolation && pass.Status == PassStatus.Stub);

        var descriptor = PassCatalog.ImplementedById(PassIds.PlatformIsolation);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        Assert.Contains(plan.Enabled, pass => pass.Id == PassIds.PlatformIsolation);
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
                PassIds.PlatformIsolation,
                PassIds.JavaKeywords,
            });

        Assert.True(plan.IsValid);
        Assert.Equal(
            new[]
            {
                PassIds.EliminateGoto,
                PassIds.PlatformIsolation,
                PassIds.JavaKeywords,
            },
            plan.Enabled.Select(pass => pass.Id).ToArray());
    }

    [Fact]
    public void ConcurrencyNormalize_precedes_platform_isolation_and_dynamic_sits_between_platform_and_java_keywords()
    {
        var ordered = PassCatalog.BuildRegistry().Ordered.ToList();

        var concurrency = ordered.FindIndex(pass => pass.Id == PassIds.ConcurrencyNormalize);
        var platform = ordered.FindIndex(pass => pass.Id == PassIds.PlatformIsolation);
        var javaKeywords = ordered.FindIndex(pass => pass.Id == PassIds.JavaKeywords);
        var dynamic = ordered.FindIndex(pass => pass.Id == PassIds.DynamicDesugar);

        Assert.True(concurrency >= 0 && platform >= 0 && javaKeywords >= 0 && dynamic >= 0);
        Assert.True(concurrency < platform, "concurrency-normalize must precede platform-isolation (locked chain)");
        // C25 flip: dynamic-desugar used to be the trailing stub; as an
        // Implemented pass its Kahn declaration slot is platform-isolation ->
        // dynamic-desugar -> java-keywords (design §3.3).
        Assert.True(platform < dynamic, "platform-isolation must precede dynamic-desugar (locked chain)");
        Assert.True(dynamic < javaKeywords, "dynamic-desugar must precede java-keywords");
    }

    [Fact]
    public void DynamicDesugar_is_Implemented_audit_default_on_and_not_a_stub()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub row is gone; the id resolves to the single Implemented
        // descriptor (the CS2J9001 stub-enable path no longer applies).
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassIds.DynamicDesugar && pass.Status == PassStatus.Stub);
        Assert.Single(registry.Ordered, pass => pass.Id == PassIds.DynamicDesugar);

        var descriptor = PassCatalog.ImplementedById(PassIds.DynamicDesugar);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(".cs2j-dynamic-desugar-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Abi, descriptor.Stage);
        Assert.Equal(new[] { PassIds.EliminateGoto }, descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
        Assert.Equal("C25", descriptor.CatalogItems);

        // Audit flipped default-on (2026-10-06): present in the default plan;
        // --disable-pass removes it while the rest of the chain stays intact.
        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        Assert.Contains(
            plan.Enabled, pass => pass.Id == PassIds.DynamicDesugar);

        var optedOut = PassPlanResolver.Resolve(
            registry, disablePassIds: new[] { PassIds.DynamicDesugar });
        Assert.True(optedOut.IsValid);
        Assert.DoesNotContain(
            optedOut.Enabled, pass => pass.Id == PassIds.DynamicDesugar);
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
                PassIds.DynamicDesugar,
                PassIds.JavaKeywords,
            });

        Assert.True(plan.IsValid);
        Assert.Equal(
            new[]
            {
                PassIds.EliminateGoto,
                PassIds.DynamicDesugar,
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
            enablePassIds: new[] { PassIds.DynamicDesugar });

        Assert.True(plan.IsValid);
        Assert.Empty(plan.Errors);
        var enabled = Assert.Single(plan.Enabled);
        Assert.Equal(PassIds.DynamicDesugar, enabled.Id);
        Assert.Equal(PassStatus.Implemented, enabled.Status);
    }

    [Fact]
    public void ListPasses_shows_dynamic_desugar_implemented_audit_default_on_with_C25_rule()
    {
        var lines = PassListFormatter.BuildLines(PassCatalog.BuildRegistry());

        var line = Assert.Single(lines, row =>
            row.TrimStart().StartsWith(
                PassIds.DynamicDesugar, StringComparison.Ordinal));
        Assert.Contains("Implemented", line);
        Assert.Contains(" on ", line);
        Assert.DoesNotContain(" off ", line);
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
    public void Resolver_rejects_enable_and_disable_conflicts_with_CS2J9003()
    {
        // W-11 (upgrade plan §6.4-R0): the enable/disable conflict carries its
        // own code (CS2J9003) instead of sharing UnknownPass/CS2J9002, so
        // profile×disable conflicts stay independently attributable.
        var plan = PassPlanResolver.Resolve(
            PassCatalog.BuildRegistry(),
            enablePassIds: new[] { PassIds.Enum },
            disablePassIds: new[] { PassIds.Enum });

        Assert.False(plan.IsValid);
        var error = Assert.Single(plan.Errors);
        Assert.StartsWith(PreprocessDiagnosticCodes.PassToggleConflict, error);
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
        Assert.Equal("C04", byId[PassIds.IteratorLowering].CatalogItems);
        // Flipped default-on on 2026-10-11 under the all-on policy.
        Assert.True(byId[PassIds.IteratorLowering].DefaultEnabled);
        Assert.Equal("C15, C34, C17, C22", byId[PassIds.NumericBoundary].CatalogItems);
        Assert.Equal("C07", byId[PassIds.StructValueSemantics].CatalogItems);
        Assert.Equal("C05", byId[PassIds.EnumDeclaration].CatalogItems);
        Assert.Equal("C06", byId[PassIds.UnsafeFfm].CatalogItems);
        Assert.Equal("C11, C12, C13, C14, C39, C46, C50, C27", byId[PassIds.MemberDesugar].CatalogItems);
        Assert.Equal("C16, C40", byId[PassIds.ForeachSource].CatalogItems);
        Assert.True(byId[PassIds.ForeachSource].DefaultEnabled);
        Assert.Equal("C47", byId[PassIds.EqualityConcat].CatalogItems);
        Assert.Equal("C23, C24, C35, C36, C51", byId[PassIds.ExpressionDesugar].CatalogItems);
        Assert.Equal("C30", byId[PassIds.ClosedMetadata].CatalogItems);
        Assert.Equal("C20", byId[PassIds.PartialMerge].CatalogItems);
        // Flipped default-on on 2026-10-11 under the all-on policy.
        Assert.True(byId[PassIds.PartialMerge].DefaultEnabled);
        Assert.Equal("C32, C45", byId[PassIds.ClosureLift].CatalogItems);
        Assert.True(byId[PassIds.ClosureLift].DefaultEnabled);
        Assert.Equal("C29", byId[PassIds.ConcurrencyNormalize].CatalogItems);
        Assert.True(byId[PassIds.ConcurrencyNormalize].DefaultEnabled);
        Assert.Equal(".cs2j-concurrency-normalize-src", byId[PassIds.ConcurrencyNormalize].IntermediateDirectoryName);
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
        Assert.Contains(baseline.Enabled, p => p.Id == PassIds.EqualityConcat);

        // convert-project: --no-equality-concat 经 legacy toggle 关回。
        var viaLegacyOff = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildLegacyToggles(
                new ConvertProjectOptions { NoEqualityConcat = true }));

        // generic: --disable-pass equality-concat.
        var viaExplicitOff = PassPlanResolver.Resolve(
            registry,
            baselineToggles,
            disablePassIds: new[] { PassIds.EqualityConcat });

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
            enablePassIds: new[] { PassIds.EqualityConcat });
        Assert.True(reEnabled.IsValid);
        Assert.Equal(
            baseline.Enabled.Select(p => p.Id).ToArray(),
            reEnabled.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void NoLinqRewrite_legacy_kill_switch_maps_to_the_linq_channel_toggle()
    {
        // C26 M4: --no-linq-rewrite is the legacy negation for linq-channel.
        var byDefault = PassChainFactory.BuildLegacyToggles(new ConvertProjectOptions());
        Assert.True(byDefault[PassIds.LinqChannel]);

        var disabled = PassChainFactory.BuildLegacyToggles(
            new ConvertProjectOptions { NoLinqRewrite = true });
        Assert.False(disabled[PassIds.LinqChannel]);
    }

    [Fact]
    public void BuildLegacyToggles_covers_every_registered_pass()
    {
        var options = new ConvertProjectOptions();
        var toggles = PassChainFactory.BuildLegacyToggles(options);

        // Legacy flags only exist for the historical implemented passes; stubs and
        // newly flipped registry passes (e.g. generic-deepen, unsafe-ffm,
        // C36 expression-desugar, C29 concurrency-normalize, C42 platform-isolation,
        // C25 dynamic-desugar/audit-default-on (flipped 2026-10-06; still no
        // legacy flag), C30 closed-metadata, C21 anonymous-type-lift,
        // C18 async-normalize/default-on,
        // C20 partial-merge/default-on (flipped 2026-10-11; still no legacy
        // flag), C04 iterator-lowering/default-on (flipped 2026-10-11),
        // C09 thread-static/default-on (flipped 2026-10-06; still no legacy
        // flag), C10 attribute-normalize/default-on (flipped 2026-10-06;
        // still no legacy flag))
        // are reachable solely through their descriptor default plus --enable-pass/--pass
        // (C04 additionally carries a legacy toggle whose default follows the descriptor;
        // C26 linq-channel flipped 2026-10-06 and carries the --no-linq-rewrite toggle).
        Assert.Equal(
            LockedChain.Where(id => id != PassIds.GenericDeepen && id != PassIds.PatternDesugar
                && id != PassIds.EnumDeclaration
                && id != PassIds.ThreadStatic
                && id != PassIds.GenericConstraint
                && id != PassIds.UnsafeFfm
                && id != PassIds.StackAllocLowering
                && id != PassIds.RefStructLowering
                && id != PassIds.RefStructSpanFieldMemoryLift
                && id != PassIds.PointerDerefIndex
                && id != PassIds.UnsafeRefLowering
                && id != PassIds.UsingResourceLift
                && id != PassIds.FixedSpanToArray
                && id != PassIds.CompatMethodReplacement
                && id != PassIds.PointerDiffInline
                && id != PassIds.ExpressionDesugar
                && id != PassIds.LexicalNormalize
                && id != PassIds.PartialMerge
                && id != PassIds.ClosureLift
                && id != PassIds.ExtensionMethods
                && id != PassIds.ConcurrencyNormalize
                && id != PassIds.VarianceAudit
                && id != PassIds.VarianceUseSiteNormalize
                && id != PassIds.PlatformIsolation
                && id != PassIds.DynamicDesugar
                && id != PassIds.ClosedMetadata
                && id != PassIds.AnonymousTypeLift
                && id != PassIds.AsyncNormalize
                && id != PassIds.AttributeNormalize
                && id != PassIds.NormalizationAudit)
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
        Assert.Contains(baseline.Enabled, p => p.Id == PassIds.ConcurrencyNormalize);

        // Explicit disable removes it; with every legacy toggle off it runs
        // alone (its eliminate-goto anchor stays disabled), which pins its
        // resolver slot.
        var allOff = LockedChain.ToDictionary(id => id, _ => false);
        var isolated = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[] { PassIds.ConcurrencyNormalize });

        Assert.True(isolated.IsValid);
        Assert.Equal(
            new[] { PassIds.ConcurrencyNormalize },
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
    public void LinqChannel_is_implemented_default_on_with_three_anchors_and_no_stub_remains()
    {
        var registry = PassCatalog.BuildRegistry();

        // The stub row is gone and the id resolves to the single Implemented
        // descriptor (the CS2J9001 stub-enable path no longer applies).
        Assert.DoesNotContain(
            registry.Ordered,
            pass => pass.Id == PassIds.LinqChannel && pass.Status == PassStatus.Stub);
        Assert.Single(registry.Ordered.Where(pass => pass.Id == PassIds.LinqChannel));

        var descriptor = PassCatalog.ImplementedById(PassIds.LinqChannel);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
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

        // M4 (2026-10-06): default-on — present in the default plan.
        var baseline = PassPlanResolver.Resolve(registry);
        Assert.True(baseline.IsValid);
        Assert.Contains(baseline.Enabled, pass => pass.Id == PassIds.LinqChannel);

        // Explicit opt-out with every toggle off still refuses to plan it
        // (the --disable-pass linq-channel / --no-linq-rewrite path).
        var disabled = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildAllDisabledToggles(registry));
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(disabled.Enabled, pass => pass.Id == PassIds.LinqChannel);

        // The concrete adapter exists for the flipped descriptor.
        var adapter = PassChainFactory.CreateAdapter(
            PassIds.LinqChannel,
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
        Assert.Equal(PassIds.LinqChannel, adapter.Descriptor.Id);
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
                PassIds.LinqChannel,
                PassIds.ConcurrencyNormalize,
            });

        Assert.True(plan.IsValid);
        Assert.Equal(
            new[]
            {
                PassIds.MemberDesugar,
                PassIds.LinqChannel,
                PassIds.ConcurrencyNormalize,
            },
            plan.Enabled.Select(pass => pass.Id).ToArray());
    }

    // ---- R6: CLI knobs + RunAfterIds enablement dependency (plan §6.4) ----

    private static PassChainConfiguration CreateR6ChainConfig(
        bool javaReady = false,
        bool genericDeepenStrict = false,
        string? genericDeepenOverrides = null,
        bool closedMetadataStrict = false)
        => new()
        {
            JavaReady = javaReady,
            GenericDeepenStrict = genericDeepenStrict,
            GenericDeepenOverridesPath = genericDeepenOverrides,
            ClosedMetadataStrict = closedMetadataStrict,
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

    [Fact]
    public void R6_new_switches_parse_on_both_entry_points()
    {
        var args = new[]
        {
            "--generic-deepen-overrides", "config/generic-deepen-overrides.json",
            "--generic-deepen-strict",
            "--closed-metadata-strict",
        };

        var preprocess = CommandLine.Parser.Default
            .ParseArguments<CSharpToJava.CLI.PreprocessVerbOptions>(args)
            .MapResult(options => options, _ => throw new InvalidOperationException("preprocess parse failed"));
        Assert.Equal("config/generic-deepen-overrides.json", preprocess.GenericDeepenOverridesPath);
        Assert.True(preprocess.GenericDeepenStrict);
        Assert.True(preprocess.ClosedMetadataStrict);

        var convert = CommandLine.Parser.Default
            .ParseArguments<CSharpToJava.CLI.ConvertProjectOptions>(
                args.Concat(new[] { "-s", "src", "-d", "dst" }))
            .MapResult(options => options, _ => throw new InvalidOperationException("convert-project parse failed"));
        Assert.Equal("config/generic-deepen-overrides.json", convert.GenericDeepenOverridesPath);
        Assert.True(convert.GenericDeepenStrict);
        Assert.True(convert.ClosedMetadataStrict);
    }

    [Fact]
    public void R6_strict_switches_reach_the_pass_options_and_cannot_weaken_the_profile()
    {
        // Default profile + explicit strict opt-ins.
        var explicitStrict = CreateR6ChainConfig(
            genericDeepenStrict: true,
            genericDeepenOverrides: "gd.json",
            closedMetadataStrict: true);
        var deepen = (CSharpToJava.CLI.Preprocessing.Adapters.GenericDeepenPassAdapter)
            PassChainFactory.CreateAdapter(PassIds.GenericDeepen, explicitStrict);
        Assert.True(deepen.Options.Strict);
        Assert.Equal("gd.json", deepen.Options.OverridesPath);

        var closed = (CSharpToJava.CLI.Preprocessing.Adapters.ClosedMetadataPassAdapter)
            PassChainFactory.CreateAdapter(PassIds.ClosedMetadata, explicitStrict);
        Assert.True(closed.Options.Strict);

        // Default profile without the switch stays non-strict.
        var relaxed = CreateR6ChainConfig();
        var relaxedDeepen = (CSharpToJava.CLI.Preprocessing.Adapters.GenericDeepenPassAdapter)
            PassChainFactory.CreateAdapter(PassIds.GenericDeepen, relaxed);
        Assert.False(relaxedDeepen.Options.Strict);
        var relaxedClosed = (CSharpToJava.CLI.Preprocessing.Adapters.ClosedMetadataPassAdapter)
            PassChainFactory.CreateAdapter(PassIds.ClosedMetadata, relaxed);
        Assert.False(relaxedClosed.Options.Strict);

        // java-ready implies strict regardless of the switch (on only).
        var javaReady = CreateR6ChainConfig(javaReady: true);
        var javaReadyDeepen = (CSharpToJava.CLI.Preprocessing.Adapters.GenericDeepenPassAdapter)
            PassChainFactory.CreateAdapter(PassIds.GenericDeepen, javaReady);
        Assert.True(javaReadyDeepen.Options.Strict);
        var javaReadyClosed = (CSharpToJava.CLI.Preprocessing.Adapters.ClosedMetadataPassAdapter)
            PassChainFactory.CreateAdapter(PassIds.ClosedMetadata, javaReady);
        Assert.True(javaReadyClosed.Options.Strict);
    }

    [Fact]
    public void R6_unknown_explicit_id_is_rejected_by_the_resolver()
    {
        var registry = PassCatalog.BuildRegistry();

        var plan = PassPlanResolver.Resolve(
            registry,
            PassChainFactory.BuildAllDisabledToggles(registry),
            new[] { "no-such-pass" });
        Assert.False(plan.IsValid);
        Assert.Contains(
            plan.Errors,
            error => error.Contains(PreprocessDiagnosticCodes.UnknownPass)
                     && error.Contains("no-such-pass"));
    }
}
