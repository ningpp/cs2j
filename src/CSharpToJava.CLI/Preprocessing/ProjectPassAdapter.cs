using System.Collections.Concurrent;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Per-run request shared by every project-level preprocess pass.
/// </summary>
internal sealed class ProjectPassRunRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;

    /// <summary>
    /// When true every pass uses the shared <see cref="ProjectPassRunner.UnifiedIntermediateDirectoryName"/>
    /// directory instead of its own per-pass intermediate directory. The first
    /// pass copies the source tree in; later passes detect that their source root
    /// already IS the unified tree and rewrite it in place, avoiding one full
    /// tree copy per pass.
    /// </summary>
    public bool UseUnifiedIntermediateDirectory { get; init; }
}

/// <summary>
/// Uniform result of running one pass over a project tree. Typed statistics and
/// diagnostics stay on the adapter; the old per-pass wrapper facades map them into
/// their historical result types.
/// </summary>
internal sealed class ProjectPassRunResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    /// <summary>True when an enabled stub was rejected before any file was touched.</summary>
    public required bool StubRejected { get; init; }

    /// <summary>Total wall-clock time for this pass (prepare + execute).</summary>
    public TimeSpan TotalElapsed { get; init; }

    /// <summary>Named sub-stage timings populated when the runner instruments each phase.</summary>
    public IReadOnlyDictionary<string, TimeSpan>? Stages { get; init; }
}

/// <summary>
/// Result of one file rewrite. Null output / Changed=false means "copy the source
/// file over unchanged".
/// </summary>
internal sealed class PassFileRewrite
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    /// <summary>Write a UTF-8 BOM when the original source file started with one.</summary>
    public bool PreserveBom { get; init; }
}

/// <summary>
/// Per-file output of the opt-in parallel rewrite path
/// (<see cref="ProjectPassAdapter.SupportsParallelFileRewrite"/>). Carries the
/// optional file rewrite plus an opaque pass-owned state object the adapter uses
/// to move per-file side effects (statistics/diagnostics) out of the parallel
/// phase and into the serial merge, so the loop's observable output stays
/// identical to the sequential path.
/// </summary>
internal sealed class PassFilePureResult
{
    public required PassFileContext Context { get; init; }

    /// <summary>Null / Changed=false means "copy the source file over unchanged".</summary>
    public PassFileRewrite? Rewrite { get; init; }

    /// <summary>
    /// Opaque per-file state produced by <see cref="ProjectPassAdapter.RewriteFilePureAsync"/>
    /// and consumed by <see cref="ProjectPassAdapter.MergeFilePureResult"/>. Null
    /// for passes with no per-file side effects to merge.
    /// </summary>
    public object? PassState { get; init; }
}

/// <summary>Everything a pass needs while processing one .cs file.</summary>
internal sealed class PassFileContext
{
    public required string SourceCode { get; init; }
    public required string SourceFilePath { get; init; }
    public required string RelativePath { get; init; }
    public required string DestinationFilePath { get; init; }
    /// <summary>Null for text-only passes (descriptor.NeedsSemanticModel == false).</summary>
    public SemanticModel? SemanticModel { get; init; }
}

/// <summary>
/// Prepared intermediate tree: copied projects, exclusion info and the enumerated
/// file set. Produced once by <see cref="ProjectPassRunner"/> and shared by every
/// pass execution.
/// </summary>
internal sealed class PassTreeContext
{
    public required ProjectPreprocessSourceLayout Layout { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string DestinationRoot { get; init; }
    public required ProjectGraph? ProjectGraph { get; init; }
    public required SupportedProjectFiltering.ExclusionInfo Exclusion { get; init; }
    public required IReadOnlyList<string> SourceFiles { get; init; }
    public required IReadOnlyList<string> CsFiles { get; init; }

    /// <summary>
    /// True when <see cref="SourceRoot"/> and <see cref="IntermediateRoot"/> are
    /// the same directory (a later pass in a unified-directory chain rewriting
    /// the tree in place).
    /// </summary>
    public required bool InPlace { get; init; }

    /// <summary>
    /// Pristine per-file state used to seed/replay/roll back scratch files when
    /// there is no separate source-tree copy. Always present; a disabled
    /// snapshot behaves as a plain <see cref="File.Copy"/>.
    /// </summary>
    public required InPlaceSourceSnapshot Snapshot { get; init; }

    /// <summary>
    /// Seed or reset a scratch file from its source-tree original. Separate-tree
    /// chains perform a byte copy; in-place (unified) chains leave the file
    /// untouched on first contact (capturing its pre-pass bytes) and afterwards
    /// restore those bytes, so multi-phase replay/rollback passes behave exactly
    /// as they do against a fresh tree copy.
    /// </summary>
    public void ResetScratchFile(string sourceFilePath, string destinationFilePath)
        => Snapshot.Reset(sourceFilePath, destinationFilePath);

    /// <summary>
    /// Syntax trees produced by the mandatory pre-parse syntax gate, keyed by
    /// full source path. <see cref="ProjectPassRunner.BuildStandardCompilation"/>
    /// reuses them so every file is read and parsed once per pass instead of
    /// twice. Populated during tree preparation; never null after that.
    /// Concurrent because the gate parses files in parallel.
    /// </summary>
    public ConcurrentDictionary<string, SyntaxTree> ParsedSyntaxTrees { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Captures the pre-pass bytes of files inside an in-place (unified)
/// intermediate tree. Multi-phase passes normally reset scratch files by
/// copying them from a separate pristine source tree; when source and
/// destination are the same directory, the pristine bytes live here instead.
/// The store is thread-safe (linq-channel drives its stage 1 in parallel).
/// </summary>
internal sealed class InPlaceSourceSnapshot
{
    private readonly ConcurrentDictionary<string, byte[]>? _bytes;

    public InPlaceSourceSnapshot(bool enabled)
    {
        _bytes = enabled ? new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) : null;
    }

    /// <summary>True when the pass is rewriting its source tree in place.</summary>
    public bool Enabled => _bytes != null;

    /// <summary>
    /// Capture the current (still pristine) bytes of a file before any pass
    /// writes to it. No-op once captured or when disabled.
    /// </summary>
    public void Capture(string sourceFilePath)
    {
        if (_bytes == null)
        {
            return;
        }

        var fullSource = Path.GetFullPath(sourceFilePath);
        _bytes.GetOrAdd(fullSource, static path => File.ReadAllBytes(path));
    }

    /// <summary>
    /// Reset a scratch destination to its source file's pre-pass state.
    /// Separate trees: byte copy. In-place tree: capture-and-leave on first
    /// contact, byte-exact restore on every later contact.
    /// </summary>
    public void Reset(string sourceFilePath, string destinationFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);

        var fullSource = Path.GetFullPath(sourceFilePath);
        var fullDestination = Path.GetFullPath(destinationFilePath);
        var samePath = string.Equals(
            fullSource, fullDestination, StringComparison.OrdinalIgnoreCase);

        if (_bytes == null)
        {
            File.Copy(sourceFilePath, destinationFilePath, overwrite: true);
            return;
        }

        if (_bytes.TryGetValue(fullSource, out var pristine))
        {
            // Captured earlier in this pass: replay/roll back to the pre-pass bytes.
            File.WriteAllBytes(fullDestination, pristine);
            return;
        }

        // First contact: the file is still in its pre-pass state. Capture it;
        // when source and destination are the same file there is nothing to do,
        // otherwise materialize the pristine bytes at the destination.
        pristine = File.ReadAllBytes(fullSource);
        if (_bytes.TryAdd(fullSource, pristine))
        {
            if (!samePath)
            {
                File.WriteAllBytes(fullDestination, pristine);
            }
        }
        else
        {
            // Lost a capture race to another thread: restore the winner's bytes.
            File.WriteAllBytes(fullDestination, _bytes[fullSource]);
        }
    }
}

/// <summary>
/// Execution environment handed to <see cref="ProjectPassAdapter.ExecuteAsync"/>.
/// Exposes the runner's composable stage helpers so custom multi-phase passes
/// (whole-tree analyze/plan/rollback, cross-file generation) can drive their own
/// iteration while reusing the exact same tree preparation / restore / verify code.
/// </summary>
internal sealed class PassExecutionContext
{
    public required ProjectPassRunRequest Request { get; init; }
    public required ProjectPassAdapter Adapter { get; init; }
    public required PassTreeContext Tree { get; init; }
    public bool Success { get; set; } = true;

    /// <summary>Accumulated sub-stage timings (filled by the runner or custom passes).</summary>
    public Dictionary<string, TimeSpan> Timings { get; } = new(StringComparer.Ordinal);

    /// <summary>Total wall-clock time for this pass execution.</summary>
    public TimeSpan TotalElapsed { get; set; }

    /// <summary>Parse + compile all .cs files with the adapter's options/references.</summary>
    public CSharpCompilation BuildStandardCompilation()
        => ProjectPassRunner.BuildStandardCompilation(Adapter, Tree);

    /// <summary>Copy a non-.cs / unchanged source file into the intermediate tree.</summary>
    public void CopyVerbatim(string sourceFilePath, string relativePath)
        => ProjectPassRunner.CopyVerbatim(Tree, sourceFilePath, relativePath);

    /// <summary>Write rewritten code; encoding honors the source BOM when requested.</summary>
    public ValueTask WriteRewrittenAsync(PassFileContext context, PassFileRewrite rewrite)
        => ProjectPassRunner.WriteRewrittenAsync(Tree, context, rewrite);

    /// <summary>dotnet restore on the intermediate tree; returns the exit code (0 = ok).</summary>
    public int Restore()
        => ProjectPassRunner.Restore(Adapter, Tree);

    /// <summary>
    /// dotnet build/test verification gated by the adapter's Verify* flags and the
    /// presence of supported test projects. Returns false when a gated step fails.
    /// </summary>
    public Task<bool> VerifyBuildAndTestsAsync()
        => ProjectPassRunner.VerifyBuildAndTestsAsync(Adapter, Tree);
}

/// <summary>
/// CLI-side adapter for one preprocess pass. The standard spine
/// (parse → compile → per-file rewrite → after-files → restore → verify) covers the
/// majority of passes; passes with custom multi-phase orchestration override
/// <see cref="ExecuteAsync"/> and drive the stage helpers on
/// <see cref="PassExecutionContext"/> themselves.
/// </summary>
internal abstract class ProjectPassAdapter
{
    /// <summary>
    /// When non-null, overrides the descriptor default. Set by the chain
    /// factory from CLI --no-verify-build / --no-verify-tests flags so that
    /// ALL passes (not just the few that thread config through their own
    /// constructor) honor the user's choice.
    /// </summary>
    private bool? _verifyBuildOverride;
    private bool? _verifyTestsOverride;

    public abstract PassDescriptor Descriptor { get; }

    /// <summary>
    /// Apply CLI verification flags. Call once after construction. Any pass
    /// that wants to keep full control can simply never call this (the
    /// property falls back to the descriptor default).
    /// </summary>
    public void ApplyVerificationConfig(bool verifyBuild, bool verifyTests)
    {
        // Only override when the CLI explicitly turned something OFF; we never
        // force-enable a descriptor-default-off pass from a bare config.
        if (!verifyBuild && Descriptor.VerifyBuildByDefault)
            _verifyBuildOverride = false;
        if (!verifyTests && Descriptor.VerifyTestsByDefault)
            _verifyTestsOverride = false;
    }

    public virtual string LogPrefix => "[" + Descriptor.Id + "]";
    public virtual bool Verbose => false;
    public virtual bool FilterUnsupportedProjects => true;
    public virtual IReadOnlyCollection<string>? CompatNamespacePrefixes => null;

    /// <summary>Run dotnet build on the intermediate tree.</summary>
    public virtual bool VerifyBuild => _verifyBuildOverride ?? Descriptor.VerifyBuildByDefault;

    /// <summary>Run dotnet test on supported test projects in the intermediate tree.</summary>
    public virtual bool VerifyTests => _verifyTestsOverride ?? Descriptor.VerifyTestsByDefault;

    /// <summary>
    /// Whether the runner deletes bin/ directories under the intermediate tree after
    /// restore/verify. Only passes that perform verification builds need this; pure
    /// rewriting passes historically left the tree untouched.
    /// </summary>
    public virtual bool DeleteBuildOutputsAfterRestore => false;

    /// <summary>Severity recorded when dotnet restore fails for the intermediate tree.</summary>
    public virtual DiagnosticSeverity RestoreFailureSeverity => DiagnosticSeverity.Warning;

    // ---- compilation customization (semantic passes) ----

    /// <summary>
    /// Called once after the intermediate tree has been prepared/copied, before the
    /// standard compilation is built. Lets passes scan the source tree (e.g. locate
    /// previously released support-library copies that must not be rewritten).
    /// </summary>
    public virtual Task OnTreePreparedAsync(PassTreeContext tree)
        => Task.CompletedTask;

    public virtual CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(preprocessorSymbols: new[] { "NETCOREAPP" });

    /// <summary>
    /// Called with the runner's verify command result so passes can preserve their
    /// historical extra console lines (e.g. generic/strip-default success messages).
    /// </summary>
    public virtual void ReportVerifyCommandResult(string verb, string targetPath, int exitCode)
    {
    }

    /// <summary>
    /// When false (historical ref/out/in behavior) verify commands run silently with
    /// captured output instead of streaming to the console, no "Verifying ..." lines
    /// are printed, and outcomes are reported through
    /// <see cref="ReportVerifyOutcome"/> instead of OnBuildFailed/OnTestsFailed.
    /// </summary>
    public virtual bool StreamVerifyOutput => true;

    /// <summary>
    /// Used only when <see cref="StreamVerifyOutput"/> is false. Receives the captured
    /// dotnet output (<c>OK:n</c> / <c>EXIT:n\n&lt;log&gt;</c>-style text is the
    /// pass' responsibility to format).
    /// </summary>
    public virtual void ReportVerifyOutcome(string verb, int exitCode, string capturedOutput)
    {
    }

    public virtual IReadOnlyCollection<MetadataReference> GetMetadataReferences()
        => ProjectUnsignedTypePreprocessor.GetBasicReferences();

    public virtual CSharpCompilationOptions CreateCompilationOptions()
        => new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);

    public virtual string CompilationName => Descriptor.Id + "-preprocess";

    /// <summary>
    /// Extra syntax trees (e.g. synthesized global usings) added to the
    /// whole-tree analysis compilation. A real build realizes
    /// <c>ImplicitUsings=enable</c> and <c>&lt;Using Include="…"/&gt;</c>
    /// items as SDK-generated GlobalUsings.g.cs files per project; the merged
    /// pass compilation has none, so every semantic pass must see the union
    /// across graph projects. Defaulting it here (instead of re-implementing
    /// the synthesis per adapter) guarantees a pass cannot silently miss a
    /// namespace — e.g. System.Text in the regex project, whose csproj
    /// declares its whole using set through <c>&lt;Using&gt;</c> items — and
    /// leave unbound residual operators/initializers behind.
    /// </summary>
    public virtual IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
    {
        var globalUsings = new System.Text.StringBuilder();
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.Layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(
                intermediateProjectFile, tree.IntermediateRoot);
            if (projectUsings != null)
            {
                globalUsings.AppendLine(projectUsings);
            }
        }

        if (globalUsings.Length == 0)
        {
            return Array.Empty<SyntaxTree>();
        }

        return new[]
        {
            CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: "__cs2j_global_usings.g.cs"),
        };
    }

    // ---- hooks ----

    /// <summary>Called once after the whole-tree compilation is built, before file rewrites.</summary>
    public virtual Task CompilationReadyAsync(CSharpCompilation compilation)
        => Task.CompletedTask;

    /// <summary>Rewrite one file; return null/Changed=false to keep the file verbatim.</summary>
    public abstract Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context);

    /// <summary>
    /// Opt-in (default false): lets the standard spine
    /// (<see cref="ProjectPassRunner.RunStandardSpineAsync"/>) run the per-file
    /// read + rewrite step in parallel. Only passes whose per-file rewrite is a
    /// pure function with no shared mutable state may enable this; a stateful
    /// pass must instead override <see cref="RewriteFilePureAsync"/> to return
    /// its per-file effects as an opaque <see cref="PassFilePureResult.PassState"/>
    /// and apply them in <see cref="MergeFilePureResult"/> (called serially, in
    /// source-file order). While false, the runner keeps the original sequential
    /// loop verbatim.
    /// </summary>
    public virtual bool SupportsParallelFileRewrite => false;

    /// <summary>
    /// Side-effect-free per-file rewrite used by the opt-in parallel path. MUST
    /// NOT mutate shared adapter state and MAY be called concurrently; the
    /// default simply wraps <see cref="RewriteFileAsync"/> and is only correct
    /// for already-pure passes.
    /// </summary>
    public virtual async Task<PassFilePureResult> RewriteFilePureAsync(PassFileContext context)
        => new() { Context = context, Rewrite = await RewriteFileAsync(context) };

    /// <summary>
    /// Serial merge hook for the opt-in parallel path: invoked once per .cs file
    /// in source-file order after the parallel rewrite phase. A stateful pass
    /// applies its per-file statistics/diagnostics here — never from the
    /// parallel phase — so summary counts and diagnostic order are unchanged.
    /// </summary>
    public virtual void MergeFilePureResult(PassFilePureResult result)
    {
    }

    /// <summary>Called after all files were rewritten; inject support libraries, distribute generated files, etc.</summary>
    public virtual Task AfterFilesAsync(PassTreeContext tree)
        => Task.CompletedTask;

    /// <summary>Record a restore failure into the pass' typed diagnostic list.</summary>
    public abstract void OnRestoreFailed(int exitCode, string restoreTarget);

    /// <summary>Record a dotnet build failure into the pass' typed diagnostic list.</summary>
    public abstract void OnBuildFailed(int exitCode);

    /// <summary>Record a dotnet test failure into the pass' typed diagnostic list.</summary>
    public abstract void OnTestsFailed(int exitCode);

    // ---- result projection ----

    public abstract bool HasErrors { get; }

    /// <summary>
    /// Single-line summary printed by convert-project (historical wording preserved
    /// per pass). Null means the chain historically printed no summary line for this
    /// pass (array-method only logs a summary in verbose standalone mode).
    /// </summary>
    public abstract string? BuildChainSummaryLine();

    /// <summary>
    /// Diagnostic lines printed by convert-project (historical formatting preserved
    /// per pass). Passes with Info-level diagnostics suppress them unless verbose.
    /// </summary>
    public abstract IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose);

    /// <summary>
    /// Optional line printed BEFORE the pass runs (e.g. array-method's verbose
    /// "Running ..." announcement). Null for every other historical pass.
    /// The adapter itself decides whether verbose mode applies.
    /// </summary>
    public virtual string? BuildChainStartLine() => null;

    /// <summary>
    /// Optional verbose line printed after the run but before the summary line
    /// (e.g. goto's "No-goto source: x -> y"). Null for every other historical pass.
    /// The adapter itself decides whether verbose mode applies.
    /// </summary>
    public virtual string? BuildChainVerbosePrelude(string inputPath, string outputPath) => null;

    /// <summary>
    /// Optional summary line printed after a successful run only in verbose mode
    /// (array-method's historical placement; the regular summary stays null for it).
    /// The adapter itself decides whether verbose mode applies.
    /// </summary>
    public virtual string? BuildChainVerboseSummaryLine() => null;

    /// <summary>
    /// Optional stderr banner printed when the pass fails (array-method's
    /// "preprocessing failed; aborting." line). Null for every other historical pass.
    /// </summary>
    public virtual string? BuildChainFailureBanner() => null;

    /// <summary>
    /// True for passes that historically reassigned (not ??) the fingerprint source
    /// pointers after running (eliminate-goto only).
    /// </summary>
    public virtual bool RepointsFingerprintToPassInput => false;

    /// <summary>
    /// Drives the pass. Defaults to the runner's standard spine; whole-tree / iterative
    /// passes override this and use the stage helpers on the context.
    /// </summary>
    public virtual Task ExecuteAsync(PassExecutionContext context)
        => ProjectPassRunner.RunStandardSpineAsync(context);
}
