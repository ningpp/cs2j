using CSharpToJava.Core.ArrayMethodRewriter;
using CSharpToJava.Core.AnonymousTypeLift;
using CSharpToJava.Core.AsyncNormalize;
using CSharpToJava.Core.AttributeNormalize;
using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.ConcurrencyNormalize;
using CSharpToJava.Core.ClosedMetadata;
using CSharpToJava.Core.DefaultParameterStripper;
using CSharpToJava.Core.DelegatePreprocessor;
using CSharpToJava.Core.DynamicDesugar;
using CSharpToJava.Core.EnumPreprocessor;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.ExtensionMethodResolve.Declarations;
using CSharpToJava.Core.ForeachDeconstruction;
using CSharpToJava.Core.ForeachSource;
using CSharpToJava.Core.GenericPreprocess;
using CSharpToJava.Core.IndexRange;
using CSharpToJava.Core.IteratorLowering;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.LexicalNormalize;
using CoreLocalFunctionLiftOptions = CSharpToJava.Core.LocalFunctionLift.LocalFunctionLiftOptions;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.NullCoalesceRewriter;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.PatternDesugar;
using CSharpToJava.Core.PartialMerge;
using CSharpToJava.Core.PlatformIsolation;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.RefOutInPreprocessor;
using CSharpToJava.Core.StructValueSemantics;
using CSharpToJava.Core.TupleToStruct;
using CSharpToJava.Core.ThreadStaticRewrite;
using CSharpToJava.Core.UnsignedTypeRewriter;
using CSharpToJava.Core.UnsafeFfm;
using CSharpToJava.Core.VarianceAudit;
using CSharpToJava.CLI.Preprocessing.Adapters;
using Microsoft.CodeAnalysis;
using CoreGenericPreprocessOptions = CSharpToJava.Core.GenericPreprocess.GenericPreprocessOptions;
using CoreTupleToStructOptions = CSharpToJava.Core.TupleToStruct.TupleToStructOptions;
using ParamsCollectionsOptions = CSharpToJava.Core.DefaultParameterStripper.ParamsCollections.ParamsCollectionsOptions;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Everything the convert-project chain needs to construct a pass adapter,
/// independent of <see cref="ConvertProjectOptions"/> parsing. Built once per
/// conversion and shared by every enabled pass.
/// </summary>
internal sealed class PassChainConfiguration
{
    public bool Verbose { get; init; }
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;

    /// <summary>
    /// Ids of every pass enabled in the running chain. numeric-boundary uses
    /// this to apply the decision-4/Q6 voucher exemption (op_Checked shells are
    /// allowed only while member-desugar co-runs).
    /// </summary>
    public IReadOnlySet<string>? EnabledPassIds { get; init; }

    /// <summary>Historical --no-local-function-strict value, shared by lift/??/enum/delegate.</summary>
    public bool Strict { get; init; } = true;

    public string TupleNamespace { get; init; } = "Cs2jTuples";

    /// <summary>Invoked only when make-readonly is enabled (keeps override loading lazy).</summary>
    public required Func<ReadOnlyStructMakerOptions> MakerOptionsFactory { get; init; }

    /// <summary>Invoked only when record-readonly is enabled (keeps override loading lazy).</summary>
    public required Func<RecordReadOnlyOptions> RecordReadOnlyOptionsFactory { get; init; }

    /// <summary>Invoked only when java-keywords is enabled (keeps override loading lazy).</summary>
    public required Func<JavaKeywordOptions> JavaKeywordOptionsFactory { get; init; }

    /// <summary>Invoked only when member-desugar is enabled (keeps override loading lazy).</summary>
    public required Func<CSharpToJava.Core.MemberDesugar.MemberDesugarOptions> MemberDesugarOptionsFactory { get; init; }

    /// <summary>Invoked only when struct-value-semantics is enabled (keeps override loading lazy).</summary>
    public required Func<StructValueSemanticsOptions> StructValueSemanticsOptionsFactory { get; init; }

    /// <summary>Invoked only when variance-audit is enabled (keeps override loading lazy).</summary>
    public required Func<VarianceAuditOptions> VarianceAuditOptionsFactory { get; init; }

    /// <summary>
    /// Invoked only when attribute-normalize is enabled (keeps overrides loading
    /// lazy). Optional so existing chain-configuration constructors stay
    /// source-compatible; the default is the built-in non-strict options.
    /// </summary>
    public Func<AttributeNormalizeOptions> AttributeNormalizeOptionsFactory { get; init; } =
        static () => new AttributeNormalizeOptions();

    /// <summary>Invoked only when enum-declaration is enabled (keeps override loading lazy).</summary>
    public required Func<CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions> EnumDeclarationOptionsFactory { get; init; }

    /// <summary>Invoked only when lexical-normalize is enabled.</summary>
    public required Func<LexicalNormalizeOptions> LexicalNormalizeOptionsFactory { get; init; }

    /// <summary>C34 decimal-ops kill-switch (enabled by default; false = --no-decimal-ops).</summary>
    public bool EnableDecimalOps { get; init; } = true;

    /// <summary>C17 increment-targets kill-switch (enabled by default).</summary>
    public bool EnableIncrementTargets { get; init; } = true;

    /// <summary>Invoked only when equality-concat is enabled (keeps override loading lazy).</summary>
    public required Func<EqualityConcatOptions> EqualityConcatOptionsFactory { get; init; }

    /// <summary>Invoked only when unsafe-ffm is enabled (keeps construction lazy).</summary>
    public required Func<UnsafeFfmOptions> UnsafeFfmOptionsFactory { get; init; }
    /// <summary>Invoked only when expression-desugar is enabled (keeps override loading lazy).</summary>
    public required Func<ExpressionDesugarOptions> ExpressionDesugarOptionsFactory { get; init; }

    /// <summary>
    /// Invoked only when platform-isolation is enabled (keeps construction lazy).
    /// Optional so existing chain-configuration constructors stay source-compatible;
    /// the default is Report mode.
    /// </summary>
    public Func<PlatformIsolationOptions> PlatformIsolationOptionsFactory { get; init; } =
        static () => new PlatformIsolationOptions();

    /// <summary>
    /// Invoked only when dynamic-desugar is enabled (keeps construction lazy).
    /// Optional so existing chain-configuration constructors stay
    /// source-compatible; the default is Audit mode (design §5.2).
    /// </summary>
    public Func<DynamicDesugarOptions> DynamicDesugarOptionsFactory { get; init; } =
        static () => new DynamicDesugarOptions();

    /// <summary>Invoked only when anonymous-type-lift is enabled (keeps construction lazy).</summary>
    public Func<AnonymousTypeLiftOptions> AnonymousTypeLiftOptionsFactory { get; init; } =
        static () => new AnonymousTypeLiftOptions();

    /// <summary>
    /// Invoked only when linq-channel is enabled (keeps construction lazy).
    /// Optional so existing chain-configuration constructors stay
    /// source-compatible (platform-isolation precedent); C26 M4 flipped the
    /// descriptor default-on (2026-10-06) and --no-linq-rewrite maps to
    /// --disable-pass linq-channel.
    /// </summary>
    public Func<LinqChannelOptions> LinqChannelOptionsFactory { get; init; } =
        static () => new LinqChannelOptions();

    /// <summary>
    /// C48 params-collections rule set inside strip-default. Null (default, rules
    /// off) keeps the pass byte-for-byte identical to its pre-C48 behavior.
    /// </summary>
    public ParamsCollectionsOptions? ParamsCollections { get; init; }

    /// <summary>
    /// M3: external-host mapping grader for the extension-methods pass
    /// (TypeMappings-backed; null when the pass is not enabled).
    /// </summary>
    public Func<INamedTypeSymbol, ExternalHostMappingKind>? ExtensionExternalHostMapper { get; init; }

    /// <summary>
    /// M4: C19-specific strict flag (design §7; default false in M4 — the pass
    /// ships off, and when explicitly enabled the Warning codes 1201-1207/
    /// 1210/1220 stay warnings unless this is set). Deliberately separate from
    /// the historical shared <see cref="Strict"/> (--no-local-function-strict).
    /// </summary>
    public bool ExtensionMethodStrict { get; init; }

    /// <summary>
    /// C20 M3: opt-in generator-absence assumption. When true, declaration-only
    /// classic partial methods and their block-level call sites may be removed.
    /// Default false keeps the pre-M3 v1 behavior (design §4.5/§9 M3).
    /// </summary>
    public bool PartialMergeAssumeNoGenerated { get; init; }

    /// <summary>M4: path to the extension-methods host overrides JSON (null = none).</summary>
    public string? ExtensionOverridesPath { get; init; }

    /// <summary>M4c: path to the closed-metadata residual-gate overrides JSON (null = none).</summary>
    public string? ClosedMetadataOverridesPath { get; init; }

    /// <summary>
    /// C49 M2: factory for extension-block materialization policy. Null
    /// (default) keeps the classic CS2J1206 gate bit-for-bit unchanged; the
    /// CLI --materialize-extension-blocks switch (both entry points) and
    /// embedded test chains opt in by returning a non-null policy. Invoked
    /// only when the extension-methods pass runs.
    /// </summary>
    public Func<ExtensionBlockOptions?>? ExtensionBlockOptionsFactory { get; init; }
}

/// <summary>
/// Maps resolved pass ids to their concrete adapters with the exact historical
/// convert-project construction defaults (option records, strict flags, verify
/// flags, compat-prefix visibility). Standalone verbs keep using the old wrapper
/// facades, which carry their own per-verb defaults.
/// </summary>
internal static class PassChainFactory
{
    /// <summary>Legacy per-pass on/off flags from the convert-project option class.</summary>
    public static IReadOnlyDictionary<string, bool> BuildLegacyToggles(ConvertProjectOptions opts)
        => new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [PassIds.Conditional] = opts.StripConditional,
            [PassIds.LocalFunctionLift] = opts.LocalFunctionLift,
            [PassIds.NullCoalesce] = opts.NullCoalesce,
            [PassIds.Enum] = opts.EnumPreprocess,
            [PassIds.Delegate] = opts.DelegatePreprocess,
            [PassIds.Tuple] = opts.TupleToStruct,
            [PassIds.ForeachDeconstruction] = opts.ForeachDeconstruction,
            [PassStubCatalog.Ids.ForeachSource] = opts.ForeachSource,
            [PassIds.StripDefault] = opts.StripDefault,
            [PassIds.RefOutIn] = opts.RefOutIn,
            [PassIds.Unsigned] = opts.UnsignedType,
            // M4: numeric-boundary defaults ON in the open gate (design §8);
            // disable explicitly with --disable-pass numeric-boundary.
            [PassStubCatalog.Ids.NumericBoundary] = true,
            [PassIds.Generic] = opts.GenericPreprocess,
            [PassIds.RecordReadOnly] = opts.RecordReadOnly,
            [PassIds.ArrayMethod] = opts.ArrayMethod,
            [PassIds.IndexRange] = true,
            [PassIds.MakeReadonly] = opts.MakeReadOnly,
            [PassIds.StructValueSemantics] = opts.StructValueSemantics,
            // Iterator-lowering follows the registry default (default-on under
            // the all-preprocessors-default-on policy). The --no-iterator-lowering
            // kill switch opts out and --enable-pass/--disable-pass win at the
            // resolver.
            [PassStubCatalog.Ids.IteratorLowering] =
                PassCatalog.ImplementedById(PassStubCatalog.Ids.IteratorLowering).DefaultEnabled
                && opts.IteratorLowering,
            [PassIds.EliminateGoto] = opts.EliminateGoto,
            [PassIds.MemberDesugar] = opts.MemberDesugar,
            [PassIds.JavaKeywords] = opts.JavaKeywords,
            // C47 equality-concat defaults ON in the open gate;
            // disable explicitly with --no-equality-concat / --disable-pass.
            [PassStubCatalog.Ids.EqualityConcat] = opts.EqualityConcatEffective,
            // C26 M4 (2026-10-06): linq-channel flipped default-on; the legacy
            // --no-linq-rewrite kill switch maps to the registry toggle
            // (--enable-pass/--disable-pass still win at the resolver).
            [PassStubCatalog.Ids.LinqChannel] =
                PassCatalog.ImplementedById(PassStubCatalog.Ids.LinqChannel).DefaultEnabled
                && opts.EnableLinqRewrite,
        };

    /// <summary>
    /// All-false legacy toggle map: used by the standalone verb's explicit
    /// <c>--pass</c> selection so that only the requested ids run.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> BuildAllDisabledToggles(PassRegistry registry)
        => registry.Ordered.ToDictionary(pass => pass.Id, _ => false, StringComparer.Ordinal);

    /// <summary>
    /// Create the adapter for an enabled, implemented pass. Throws for unknown ids
    /// or stubs (stubs are rejected by the caller before any adapter is requested).
    /// </summary>
    public static ProjectPassAdapter CreateAdapter(string passId, PassChainConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ProjectPassAdapter adapter = passId switch
        {
            PassIds.Conditional => new ConditionalPassAdapter(
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                // The upstream test suites run under `dotnet test`'s default Debug
                // configuration: `#if DEBUG` members (RegexCode.ToString/Dump,
                // RegexOptions.Debug, IsDebug, …) are part of the tested surface.
                // Stripping to Release silently deletes them and the converted
                // tests fail against members that no longer exist.
                configuration: "Debug",
                tfm: null,
                removeRegions: false),

            PassStubCatalog.Ids.LexicalNormalize => new LexicalNormalizePassAdapter(
                config.LexicalNormalizeOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            PassIds.LocalFunctionLift => new LocalFunctionLiftPassAdapter(
                new CoreLocalFunctionLiftOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassIds.NullCoalesce => new NullCoalescePassAdapter(
                new NullCoalesceOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassIds.Enum => new EnumPassAdapter(
                new EnumPreprocessorOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassIds.EnumDeclaration => new EnumDeclarationPassAdapter(
                config.EnumDeclarationOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassIds.Delegate => new DelegatePassAdapter(
                new DelegatePreprocessorOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassIds.Tuple => new TuplePassAdapter(
                new CoreTupleToStructOptions { NamespaceName = config.TupleNamespace },
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            // C21 has no convert-project legacy toggle; the descriptor default
            // (off, observation period per design §8) drives it and
            // --enable-pass anonymous-type-lift opts in.
            PassIds.AnonymousTypeLift => new AnonymousTypeLiftPassAdapter(
                config.AnonymousTypeLiftOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            PassIds.ForeachDeconstruction => new ForeachDeconstructionPassAdapter(
                new ForeachDeconstructionOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            PassStubCatalog.Ids.ForeachSource => new ForeachSourcePassAdapter(
                new ForeachSourceOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.Strict),

            PassIds.PatternDesugar => new PatternDesugarPassAdapter(
                new PatternDesugarOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassIds.StripDefault => new StripDefaultPassAdapter(
                new DefaultParameterStripperOptions(ParamsCollections: config.ParamsCollections),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            PassIds.RefOutIn => new RefOutInPassAdapter(
                new RefOutInRewriterOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            PassIds.Unsigned => new UnsignedPassAdapter(
                new UnsignedTypeRewriterOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            PassStubCatalog.Ids.NumericBoundary => new NumericBoundaryPassAdapter(
                new NumericBoundaryOptions
                {
                    EnabledPassIds = config.EnabledPassIds,
                    EnableDecimalOps = config.EnableDecimalOps,
                    EnableIncrementTargets = config.EnableIncrementTargets,
                },
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassIds.Generic => new GenericPassAdapter(
                new CoreGenericPreprocessOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            PassIds.GenericDeepen => new GenericDeepenPassAdapter(
                new GenericDeepenOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            // C09 thread-static has no convert-project legacy toggle; the
            // descriptor default (OFF, design §3.1/§12) drives it and
            // --enable-pass thread-static / --pass thread-static opts in.
            // Info diagnostics (TS001/TS002) are emitted only in verbose mode.
            PassIds.ThreadStatic => new ThreadStaticPassAdapter(
                new ThreadStaticOptions
                {
                    Strict = config.Strict,
                    ReportInfo = config.Verbose,
                },
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            PassIds.VarianceAudit => new VarianceAuditPassAdapter(
                config.VarianceAuditOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            PassIds.ClosedMetadata => new ClosedMetadataPassAdapter(
                new ClosedMetadataOptions
                {
                    EnabledPassIds = config.EnabledPassIds,
                    OverridesPath = config.ClosedMetadataOverridesPath,
                },
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            PassIds.RecordReadOnly => new RecordReadOnlyPassAdapter(
                config.RecordReadOnlyOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            // Historically the chain did not pass compat namespace prefixes to array-method.
            PassIds.ArrayMethod => new ArrayMethodPassAdapter(
                new ArrayMethodRewriterOptions(Verbose: config.Verbose),
                config.Verbose,
                config.FilterUnsupportedProjects,
                compatNamespacePrefixes: null),

            PassIds.IndexRange => new IndexRangePassAdapter(
                new IndexRangeOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassIds.MakeReadonly => new MakeReadonlyPassAdapter(
                config.MakerOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            // C10 has no convert-project legacy toggle; the descriptor default
            // (off, opt-in) drives it and --enable-pass/--pass opts in
            // (platform-isolation precedent). Overrides load lazily only when
            // the pass runs.
            PassIds.AttributeNormalize => new AttributeNormalizePassAdapter(
                config.AttributeNormalizeOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            PassIds.StructValueSemantics => new StructValueSemanticsPassAdapter(
                config.StructValueSemanticsOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            PassStubCatalog.Ids.UnsafeFfm => new UnsafeFfmPassAdapter(
                config.UnsafeFfmOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            PassStubCatalog.Ids.PartialMerge => new PartialMergePassAdapter(
                new PartialMergeOptions
                {
                    AssumeNoGeneratedPartial = config.PartialMergeAssumeNoGenerated,
                },
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            PassStubCatalog.Ids.ClosureLift => new ClosureLiftPassAdapter(
                new ClosureLiftOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassStubCatalog.Ids.IteratorLowering => new IteratorLoweringPassAdapter(
                new IteratorLoweringOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            // C18 async-normalize has no convert-project legacy toggle; the
            // descriptor default (off, long-term opt-in) drives it and
            // --enable-pass/--pass opts in (concurrency-normalize /
            // platform-isolation registry-default precedent).
            PassStubCatalog.Ids.AsyncNormalize => new AsyncNormalizePassAdapter(
                new AsyncNormalizeOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            // C19 has no convert-project legacy toggle; the descriptor default
            // (off) drives it and --enable-pass extension-methods opts in.
            // M4: strict is a C19-dedicated flag (default false); overrides
            // load lazily only when the pass runs.
            PassIds.ExtensionMethods => new ExtensionMethodResolvePassAdapter(
                new ExtensionMethodResolveOptions { Strict = config.ExtensionMethodStrict },
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.ExtensionExternalHostMapper,
                ExtensionHostOverridesFile.Load(config.ExtensionOverridesPath),
                blockOptions: config.ExtensionBlockOptionsFactory?.Invoke(),
                memberDesugarChainAvailable:
                    config.EnabledPassIds?.Contains(PassIds.MemberDesugar) == true),

            // convert-project historically ran goto with Strict=false.
            PassIds.EliminateGoto => new GotoPassAdapter(
                config.Verbose,
                strict: false,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            PassIds.JavaKeywords => new JavaKeywordsPassAdapter(
                config.JavaKeywordOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            PassIds.MemberDesugar => new MemberDesugarPassAdapter(
                config.MemberDesugarOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes),

            // C26 has no convert-project legacy toggle; the descriptor
            // default (off) drives it and --enable-pass linq-channel opts in
            // (platform-isolation / anonymous-type-lift precedent).
            PassStubCatalog.Ids.LinqChannel => new LinqChannelPassAdapter(
                config.LinqChannelOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            // C29: strict defaults to false in convert-project (goto precedent).
            // Non-canonical Monitor structures (CS2J2910) are legitimate skips —
            // the code is left unchanged as valid C#; escalating them to pass-
            // failing Errors would abort the entire chain on real-world code that
            // uses batch Enter/Exit in loops or manual TryEnter/Exit pairing.
            // The ConcurrencyOptions.Strict flag and the adapter's strict
            // parameter both receive false so CS2J2910/2911/2920/2921 stay
            // Warnings; a future --concurrency-strict opt-in can restore the
            // strict CI gate (linq-channel precedent).
            PassStubCatalog.Ids.ConcurrencyNormalize => new ConcurrencyNormalizePassAdapter(
                new ConcurrencyOptions { Strict = false },
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                strict: false),

            PassStubCatalog.Ids.EqualityConcat => new EqualityConcatPassAdapter(
                config.EqualityConcatOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            // C36 expression-desugar has no convert-project legacy toggle; the
            // descriptor default (on) drives it and --disable-pass opts out
            // (enum-declaration precedent). member-desugar co-runs in the
            // default chain and rescues preserved init-only initializer sites
            // (widens init→set + PD-8 lifting), so the C24 rule downgrades
            // init-only CS2J0531 to a Warning in that chain composition.
            PassStubCatalog.Ids.ExpressionDesugar => new ExpressionDesugarPassAdapter(
                config.ExpressionDesugarOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                memberDesugarChainAvailable:
                    config.EnabledPassIds?.Contains(PassIds.MemberDesugar) == true),

            // C42 platform-isolation has no convert-project legacy toggle; the
            // descriptor default (off) drives it and --enable-pass opts in.
            PassStubCatalog.Ids.PlatformIsolation => new PlatformIsolationPassAdapter(
                config.PlatformIsolationOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            // C25 dynamic-desugar has no convert-project legacy toggle; the
            // descriptor default (off, long-term opt-in per design §3.2/§8)
            // drives it and --enable-pass opts in. M1 ships the Audit-mode
            // adapter only (zero rewrites).
            PassStubCatalog.Ids.DynamicDesugar => new DynamicDesugarPassAdapter(
                config.DynamicDesugarOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.VerifyBuild,
                config.VerifyTests),

            _ => throw new KeyNotFoundException($"No chain adapter is registered for pass id '{passId}'."),
        };

        // Push CLI --no-verify-build / --no-verify-tests to every adapter so
        // that even passes which don't take a verifyTests ctor arg honor the
        // user's choice (the adapter property falls back to descriptor default
        // for passes we never call this on).
        adapter.ApplyVerificationConfig(config.VerifyBuild, config.VerifyTests);
        return adapter;
    }

    /// <summary>
    /// Whether a workspace project path is an injected support library belonging to
    /// a pass that actually ran. Such projects are symbol sources only and must not
    /// be planned as Maven modules or enter the Java conversion.
    ///
    /// C19 M3 (appendix ruling 7): Cs2jPrelude (delegate/equality-concat
    /// injection) is recognized the same way as the other three support
    /// projects. Cs2jTuples has no project of its own — its generated struct
    /// files are distributed into user projects and stay user-owned.
    /// </summary>
    public static bool IsExecutedSupportLibraryProject(
        IReadOnlySet<PassSupportLibrary> executedLibraries,
        string projectFilePath)
    {
        return (executedLibraries.Contains(PassSupportLibrary.Cs2jUnsignedMath)
                && Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(projectFilePath))
            || (executedLibraries.Contains(PassSupportLibrary.Cs2jArray)
                && Cs2jArrayProject.IsCs2jArrayProject(projectFilePath))
            || (executedLibraries.Contains(PassSupportLibrary.Cs2jRefOutIn)
                && Cs2jRefOutInProject.IsCs2jRefOutInProject(projectFilePath))
            || (executedLibraries.Contains(PassSupportLibrary.Cs2jPrelude)
                && Cs2jPreludeProject.IsCs2jPreludeProject(projectFilePath));
    }
}
