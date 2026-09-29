using CSharpToJava.Core.ArrayMethodRewriter;
using CSharpToJava.Core.DefaultParameterStripper;
using CSharpToJava.Core.DelegatePreprocessor;
using CSharpToJava.Core.EnumPreprocessor;
using CSharpToJava.Core.ForeachDeconstruction;
using CSharpToJava.Core.GenericPreprocess;
using CSharpToJava.Core.IteratorLowering;
using CSharpToJava.Core.JavaKeywords;
using CoreLocalFunctionLiftOptions = CSharpToJava.Core.LocalFunctionLift.LocalFunctionLiftOptions;
using CSharpToJava.Core.NullCoalesceRewriter;
using CSharpToJava.Core.PatternDesugar;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.RefOutInPreprocessor;
using CSharpToJava.Core.TupleToStruct;
using CSharpToJava.Core.UnsignedTypeRewriter;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CoreGenericPreprocessOptions = CSharpToJava.Core.GenericPreprocess.GenericPreprocessOptions;
using CoreTupleToStructOptions = CSharpToJava.Core.TupleToStruct.TupleToStructOptions;

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
            [PassIds.StripDefault] = opts.StripDefault,
            [PassIds.RefOutIn] = opts.RefOutIn,
            [PassIds.Unsigned] = opts.UnsignedType,
            [PassIds.Generic] = opts.GenericPreprocess,
            [PassIds.RecordReadOnly] = opts.RecordReadOnly,
            [PassIds.ArrayMethod] = opts.EnableArrayMethod,
            [PassIds.MakeReadonly] = opts.MakeReadOnly,
            [PassStubCatalog.Ids.IteratorLowering] = false,
            [PassIds.EliminateGoto] = opts.EliminateGoto,
            [PassIds.MemberDesugar] = opts.MemberDesugar,
            [PassIds.JavaKeywords] = opts.JavaKeywords,
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

            PassIds.PatternDesugar => new PatternDesugarPassAdapter(
                new PatternDesugarOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

            PassIds.StripDefault => new StripDefaultPassAdapter(
                new DefaultParameterStripperOptions(),
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

            PassIds.MakeReadonly => new MakeReadonlyPassAdapter(
                config.MakerOptionsFactory(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests),

            PassStubCatalog.Ids.IteratorLowering => new IteratorLoweringPassAdapter(
                new IteratorLoweringOptions(),
                config.Verbose,
                config.FilterUnsupportedProjects,
                config.CompatNamespacePrefixes,
                config.VerifyBuild,
                config.VerifyTests,
                config.Strict),

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

            _ => throw new KeyNotFoundException($"No chain adapter is registered for pass id '{passId}'."),
        };
    }

    /// <summary>
    /// Whether a workspace project path is an injected support library belonging to
    /// a pass that actually ran. Such projects are symbol sources only and must not
    /// be planned as Maven modules or enter the Java conversion.
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
                && Cs2jRefOutInProject.IsCs2jRefOutInProject(projectFilePath));
    }
}
