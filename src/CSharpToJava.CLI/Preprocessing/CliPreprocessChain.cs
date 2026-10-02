using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using CSharpToJava.Core.UnsafeFfm;

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

    /// <summary>Invoked only when enum-declaration is enabled (keeps override loading lazy).</summary>
    public required Func<CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions> EnumDeclarationOptionsFactory { get; init; }

    /// <summary>Invoked only when equality-concat is enabled (keeps override loading lazy).</summary>
    public required Func<EqualityConcatOptions> EqualityConcatOptionsFactory { get; init; }

    /// <summary>Invoked only when unsafe-ffm is enabled (keeps construction lazy).</summary>
    public required Func<UnsafeFfmOptions> UnsafeFfmOptionsFactory { get; init; }

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
    /// C34: opt-in decimal operator normalization inside numeric-boundary.
    /// Dormant default (false); tests and M3 flip it explicitly.
    /// </summary>
    public bool EnableDecimalOps { get; init; }

    /// <summary>
    /// C17: increment-targets read-modify-write normalization inside
    /// numeric-boundary. Enabled by default; set false to exercise the
    /// family kill-switch.
    /// </summary>
    public bool EnableIncrementTargets { get; init; } = true;
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
            EnumDeclarationOptionsFactory = request.EnumDeclarationOptionsFactory,
            EnableDecimalOps = request.EnableDecimalOps,
            EnableIncrementTargets = request.EnableIncrementTargets,
            EqualityConcatOptionsFactory = request.EqualityConcatOptionsFactory,
            UnsafeFfmOptionsFactory = request.UnsafeFfmOptionsFactory,
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
