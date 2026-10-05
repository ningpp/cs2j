using CSharpToJava.Core.DynamicDesugar;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.ExtensionMethodResolve.Declarations;
using CSharpToJava.Core.AttributeNormalize;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.PlatformIsolation;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using CSharpToJava.Core.VarianceAudit;
using ParamsCollectionsOptions = CSharpToJava.Core.DefaultParameterStripper.ParamsCollections.ParamsCollectionsOptions;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Inputs for one CLI-driven run of the registry-ordered preprocess chain,
/// shared by <c>convert-project</c> and the standalone <c>preprocess</c> verb.
/// </summary>
internal sealed class CliChainRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
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

    /// <summary>Invoked only when struct-value-semantics is enabled (keeps override loading lazy).</summary>
    public required Func<StructValueSemanticsOptions> StructValueSemanticsOptionsFactory { get; init; }

    /// <summary>
    /// Invoked only when variance-audit is enabled. Defaulted so callers that never
    /// enable the pass (and existing standalone verbs) need not supply one.
    /// </summary>
    public Func<VarianceAuditOptions> VarianceAuditOptionsFactory { get; init; }
        = () => new VarianceAuditOptions();

    /// <summary>
    /// Invoked only when attribute-normalize is enabled. Defaulted so callers that
    /// never enable the pass need not supply one.
    /// </summary>
    public Func<AttributeNormalizeOptions> AttributeNormalizeOptionsFactory { get; init; }
        = () => new AttributeNormalizeOptions();

    /// <summary>Invoked only when enum-declaration is enabled (keeps override loading lazy).</summary>
    public required Func<CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions> EnumDeclarationOptionsFactory { get; init; }

    /// <summary>Invoked only when lexical-normalize is enabled.</summary>
    public required Func<LexicalNormalizeOptions> LexicalNormalizeOptionsFactory { get; init; }

    /// <summary>Invoked only when equality-concat is enabled (keeps override loading lazy).</summary>
    public required Func<EqualityConcatOptions> EqualityConcatOptionsFactory { get; init; }

    /// <summary>Invoked only when unsafe-ffm is enabled (keeps construction lazy).</summary>
    public required Func<UnsafeFfmOptions> UnsafeFfmOptionsFactory { get; init; }
    /// <summary>Invoked only when expression-desugar is enabled (keeps override loading lazy).</summary>
    public required Func<ExpressionDesugarOptions> ExpressionDesugarOptionsFactory { get; init; }

    /// <summary>
    /// Invoked only when platform-isolation is enabled (keeps construction lazy).
    /// Optional so existing chain-request constructors stay source-compatible;
    /// the default is Report mode.
    /// </summary>
    public Func<PlatformIsolationOptions> PlatformIsolationOptionsFactory { get; init; } =
        static () => new PlatformIsolationOptions();

    /// <summary>
    /// Invoked only when dynamic-desugar is enabled (keeps construction lazy).
    /// Optional so existing chain-request constructors stay source-compatible;
    /// the default is Audit mode (design §5.2).
    /// </summary>
    public Func<DynamicDesugarOptions> DynamicDesugarOptionsFactory { get; init; } =
        static () => new DynamicDesugarOptions();

    /// Invoked only when linq-channel is enabled. Defaulted so existing
    /// chain-request call sites stay source-compatible (C26 M1 is
    /// default-off; platform-isolation precedent).
    /// </summary>
    public Func<LinqChannelOptions> LinqChannelOptionsFactory { get; init; } =
        static () => new LinqChannelOptions();

    /// <summary>
    /// Per-id on/off state from legacy flags. Null means "use descriptor defaults";
    /// the standalone verb passes an all-disabled map together with explicit ids.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? LegacyToggles { get; init; }

    /// <summary>Explicit --enable-pass / --pass overrides.</summary>
    public IEnumerable<string>? EnablePassIds { get; init; }

    /// <summary>Explicit --disable-pass overrides.</summary>
    public IEnumerable<string>? DisablePassIds { get; init; }

    /// <summary>
    /// C34: decimal operator normalization inside numeric-boundary. Enabled by
    /// default; set false for the family kill-switch (<c>--no-decimal-ops</c>).
    /// </summary>
    public bool EnableDecimalOps { get; init; } = true;

    /// <summary>
    /// C17: increment-targets read-modify-write normalization inside
    /// numeric-boundary. Enabled by default; set false to exercise the
    /// family kill-switch.
    /// </summary>
    public bool EnableIncrementTargets { get; init; } = true;

    /// <summary>
    /// C48 params-collections rule set inside strip-default. Null (default,
    /// rules off) keeps the pass byte-for-byte identical to its pre-C48 behavior.
    /// </summary>
    public ParamsCollectionsOptions? ParamsCollections { get; init; }

    /// <summary>
    /// M3: TypeMappings config path used to build the extension-methods
    /// external-host grader (null/empty → the registry's default config path).
    /// Only consumed when the extension-methods pass is enabled.
    /// </summary>
    public string? ExtensionMappingConfigPath { get; init; }

    /// <summary>M4: C19-dedicated strict (default false; Warning codes stay warnings).</summary>
    public bool ExtensionMethodStrict { get; init; }

    /// <summary>M4: extension-methods host overrides JSON path (null = none).</summary>
    public string? ExtensionOverridesPath { get; init; }

    /// <summary>
    /// C49 M2: extension-block materialization policy factory (null =
    /// classic CS2J1206 behavior). Both CLI entry points supply it from
    /// --materialize-extension-blocks; embedded chains may also set it.
    /// </summary>
    public Func<ExtensionBlockOptions?>? ExtensionBlockOptionsFactory { get; init; }
}

/// <summary>
/// Outcome of a chain run: the final source pointer, the sets describing what
/// actually executed, and the goto-specific fingerprint repoint (goto was the
/// only historical pass that reassigned the fingerprint pointers outright).
/// </summary>
internal sealed class CliChainResult
{
    public required bool Success { get; init; }
    public required string FinalSourcePath { get; init; }
    public bool UsingGotoPreprocessedSource { get; init; }

    /// <summary>eliminate-goto's own input path (its historical fingerprint source assignment).</summary>
    public string? GotoRepointedSourcePath { get; init; }

    /// <summary>eliminate-goto input layout source root (its historical fingerprint root assignment).</summary>
    public string? GotoRepointedInputRoot { get; init; }

    public required IReadOnlySet<string> ExecutedIntermediateDirectoryNames { get; init; }
    public required IReadOnlySet<PassSupportLibrary> ExecutedSupportLibraries { get; init; }
}

/// <summary>
/// Resolves the pass plan and runs it in locked topological order, preserving
/// every historical summary/diagnostic wording through the adapter hooks.
/// </summary>
internal static class CliPreprocessChain
{
    /// <summary>
    /// C26 §5.3 double-run suppression — the ONLY converter-side wiring added
    /// during the channel/E16 coexistence period. When the executed chain
    /// materialized the linq-channel intermediate tree, that tree already
    /// carries the procedural LINQ output; the downstream conversion must not
    /// run the embedded E16 rewriter a second time (skipped chains would be
    /// retried and helpers extracted twice). One condition, no new switch:
    /// when the channel did not run, E16 behaves exactly as before. The single
    /// ConversionOptions built from this answer feeds all three convert-project
    /// downstream paths (workspace / project-graph / manual directory scan).
    /// </summary>
    public static bool IsEmbeddedLinqRewriteSuppressed(
        IReadOnlySet<string>? executedIntermediateDirectoryNames)
        => executedIntermediateDirectoryNames is not null
        && executedIntermediateDirectoryNames.Contains(
            Adapters.LinqChannelPassAdapter.IntermediateDirectoryName);

    public static async Task<CliChainResult> RunAsync(CliChainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var registry = PassCatalog.BuildRegistry();
        var plan = PassPlanResolver.Resolve(
            registry,
            request.LegacyToggles,
            request.EnablePassIds,
            request.DisablePassIds);

        var emptyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emptyLibraries = new HashSet<PassSupportLibrary>();

        if (!plan.IsValid)
        {
            foreach (var planError in plan.Errors)
            {
                Console.Error.WriteLine(planError);
            }

            return Failed(request.SourcePath, emptyNames, emptyLibraries);
        }

        var executedIntermediateDirectoryNames = plan.Enabled
            .Select(pass => pass.IntermediateDirectoryName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var executedSupportLibraries = plan.Enabled
            .Where(pass => pass.SupportLibrary != PassSupportLibrary.None)
            .Select(pass => pass.SupportLibrary)
            .ToHashSet();

        // M3: build the TypeMappings-backed external host grader only when C19
        // actually runs (keeps registry load out of every unrelated chain run).
        Func<INamedTypeSymbol, ExternalHostMappingKind>? extensionExternalHostMapper = null;
        if (plan.Enabled.Any(pass => pass.Id == PassIds.ExtensionMethods))
        {
            extensionExternalHostMapper = ExtensionHostMappingResolver.TryCreate(
                request.ExtensionMappingConfigPath);
        }
        // passPresent bits for the audit report (design D13): whether E10 (generic)
        // and C02 (generic-deepen) are on the running chain. Injected into the audit
        // options here; a standalone `preprocess --pass variance-audit` run sees
        // neither and reports both bits false.
        var chainHasGeneric = plan.Enabled.Any(pass => pass.Id == PassIds.Generic);
        var chainHasGenericDeepen = plan.Enabled.Any(pass => pass.Id == PassIds.GenericDeepen);

        var config = new PassChainConfiguration
        {
            Verbose = request.Verbose,
            FilterUnsupportedProjects = request.FilterUnsupportedProjects,
            CompatNamespacePrefixes = request.CompatNamespacePrefixes,
            VerifyBuild = request.VerifyBuild,
            VerifyTests = request.VerifyTests,
            Strict = request.Strict,
            TupleNamespace = request.TupleNamespace,
            EnabledPassIds = plan.Enabled.Select(pass => pass.Id).ToHashSet(StringComparer.Ordinal),
            MakerOptionsFactory = request.MakerOptionsFactory,
            RecordReadOnlyOptionsFactory = request.RecordReadOnlyOptionsFactory,
            JavaKeywordOptionsFactory = request.JavaKeywordOptionsFactory,
            MemberDesugarOptionsFactory = request.MemberDesugarOptionsFactory,
            StructValueSemanticsOptionsFactory = request.StructValueSemanticsOptionsFactory,
            VarianceAuditOptionsFactory = () =>
            {
                var options = request.VarianceAuditOptionsFactory();
                options.ChainHasGeneric = chainHasGeneric;
                options.ChainHasGenericDeepen = chainHasGenericDeepen;
                return options;
            },
            EnumDeclarationOptionsFactory = request.EnumDeclarationOptionsFactory,
            AttributeNormalizeOptionsFactory = request.AttributeNormalizeOptionsFactory,
            LexicalNormalizeOptionsFactory = request.LexicalNormalizeOptionsFactory,
            EnableDecimalOps = request.EnableDecimalOps,
            EnableIncrementTargets = request.EnableIncrementTargets,
            EqualityConcatOptionsFactory = request.EqualityConcatOptionsFactory,
            UnsafeFfmOptionsFactory = request.UnsafeFfmOptionsFactory,
            ExpressionDesugarOptionsFactory = request.ExpressionDesugarOptionsFactory,
            PlatformIsolationOptionsFactory = request.PlatformIsolationOptionsFactory,
            DynamicDesugarOptionsFactory = request.DynamicDesugarOptionsFactory,
            LinqChannelOptionsFactory = request.LinqChannelOptionsFactory,
            ParamsCollections = request.ParamsCollections,
            ExtensionExternalHostMapper = extensionExternalHostMapper,
            ExtensionMethodStrict = request.ExtensionMethodStrict,
            ExtensionOverridesPath = request.ExtensionOverridesPath,
            ExtensionBlockOptionsFactory = request.ExtensionBlockOptionsFactory,
        };

        var sourcePath = request.SourcePath;
        string? gotoRepointedSourcePath = null;
        string? gotoRepointedInputRoot = null;
        var usingGotoPreprocessedSource = false;

        foreach (var pass in plan.Enabled)
        {
            if (pass.Status == PassStatus.Stub)
            {
                // Enabled stub: fail loudly before touching the filesystem.
                Console.Error.WriteLine(ProjectPassRunner.StubMessage(pass));
                return Failed(sourcePath, executedIntermediateDirectoryNames, executedSupportLibraries);
            }

            var sourceBeforePass = sourcePath;
            var adapter = PassChainFactory.CreateAdapter(pass.Id, config);

            var startLine = adapter.BuildChainStartLine();
            if (startLine != null)
            {
                Console.WriteLine(startLine);
            }

            var run = await ProjectPassRunner.RunAsync(
                adapter,
                new ProjectPassRunRequest
                {
                    SourcePath = sourcePath,
                    DestinationRoot = request.DestinationRoot,
                    Force = request.Force,
                });

            var preludeLine = adapter.BuildChainVerbosePrelude(
                sourceBeforePass, run.PreprocessedSourcePath);
            if (preludeLine != null)
            {
                Console.WriteLine(preludeLine);
            }

            var summaryLine = adapter.BuildChainSummaryLine();
            if (summaryLine != null)
            {
                Console.WriteLine(summaryLine);
            }

            foreach (var diagnosticLine in adapter.BuildChainDiagnosticLines(request.Verbose))
            {
                Console.Error.WriteLine(diagnosticLine);
            }

            if (!run.Success)
            {
                var failureBanner = adapter.BuildChainFailureBanner();
                if (failureBanner != null)
                {
                    Console.Error.WriteLine(failureBanner);
                }

                return Failed(sourcePath, executedIntermediateDirectoryNames, executedSupportLibraries);
            }

            sourcePath = run.PreprocessedSourcePath;
            if (adapter.RepointsFingerprintToPassInput)
            {
                // eliminate-goto historically reassigned the fingerprint pointers.
                gotoRepointedSourcePath = sourceBeforePass;
                gotoRepointedInputRoot = run.SourceRoot;
                usingGotoPreprocessedSource = true;
            }

            var verboseSummaryLine = adapter.BuildChainVerboseSummaryLine();
            if (verboseSummaryLine != null)
            {
                Console.WriteLine(verboseSummaryLine);
            }
        }

        return new CliChainResult
        {
            Success = true,
            FinalSourcePath = sourcePath,
            UsingGotoPreprocessedSource = usingGotoPreprocessedSource,
            GotoRepointedSourcePath = gotoRepointedSourcePath,
            GotoRepointedInputRoot = gotoRepointedInputRoot,
            ExecutedIntermediateDirectoryNames = executedIntermediateDirectoryNames,
            ExecutedSupportLibraries = executedSupportLibraries,
        };
    }

    private static CliChainResult Failed(
        string sourcePath,
        IReadOnlySet<string> intermediateDirectoryNames,
        IReadOnlySet<PassSupportLibrary> supportLibraries)
        => new()
        {
            Success = false,
            FinalSourcePath = sourcePath,
            ExecutedIntermediateDirectoryNames = intermediateDirectoryNames,
            ExecutedSupportLibraries = supportLibraries,
        };
}
