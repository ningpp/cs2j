using CSharpToJava.Core.ArrayMethodRewriter;
using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.ConcurrencyNormalize;
using CSharpToJava.Core.DefaultParameterStripper;
using CSharpToJava.Core.DelegatePreprocessor;
using CSharpToJava.Core.EnumPreprocessor;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.ForeachDeconstruction;
using CSharpToJava.Core.ForeachSource;
using CSharpToJava.Core.GenericPreprocess;
using CSharpToJava.Core.IndexRange;
using CSharpToJava.Core.IteratorLowering;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LexicalNormalize;
using CoreLocalFunctionLiftOptions = CSharpToJava.Core.LocalFunctionLift.LocalFunctionLiftOptions;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.NullCoalesceRewriter;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.PatternDesugar;
using CSharpToJava.Core.PartialMerge;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.RefOutInPreprocessor;
using CSharpToJava.Core.StructValueSemantics;
using CSharpToJava.Core.TupleToStruct;
using CSharpToJava.Core.UnsignedTypeRewriter;
using CSharpToJava.Core.UnsafeFfm;
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

    /// <summary>M4: path to the extension-methods host overrides JSON (null = none).</summary>
    public string? ExtensionOverridesPath { get; init; }
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
            [PassStubCatalog.Ids.IteratorLowering] = opts.IteratorLowering,
            [PassIds.EliminateGoto] = opts.EliminateGoto,
            [PassIds.MemberDesugar] = opts.MemberDesugar,
            [PassIds.JavaKeywords] = opts.JavaKeywords,
            // C47 equality-concat defaults ON in the open gate;
            // disable explicitly with --no-equality-concat / --disable-pass.
            [PassStubCatalog.Ids.EqualityConcat] = opts.EqualityConcatEffective,
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
        return passId switch
        {
            PassIds.Conditional => new ConditionalPassAdapter(
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                configuration: "Release",
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
                new PartialMergeOptions(),
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
                ExtensionHostOverridesFile.Load(config.ExtensionOverridesPath)),

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

            // C29 has no convert-project legacy toggle; the descriptor default
            // (off, long-term opt-in) drives it and --enable-pass/--pass opts in
            // (expression-desugar precedent).
            PassStubCatalog.Ids.ConcurrencyNormalize => new ConcurrencyNormalizePassAdapter(
                new ConcurrencyOptions { Strict = config.Strict },
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

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
            // (enum-declaration precedent).
            PassStubCatalog.Ids.ExpressionDesugar => new ExpressionDesugarPassAdapter(
                config.ExpressionDesugarOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            _ => throw new KeyNotFoundException($"No chain adapter is registered for pass id '{passId}'."),
        };
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
