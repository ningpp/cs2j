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
    public abstract PassDescriptor Descriptor { get; }

    public virtual string LogPrefix => "[" + Descriptor.Id + "]";
    public virtual bool Verbose => false;
    public virtual bool FilterUnsupportedProjects => true;
    public virtual IReadOnlyCollection<string>? CompatNamespacePrefixes => null;

    /// <summary>Run dotnet build on the intermediate tree.</summary>
    public virtual bool VerifyBuild => Descriptor.VerifyBuildByDefault;

    /// <summary>Run dotnet test on supported test projects in the intermediate tree.</summary>
    public virtual bool VerifyTests => Descriptor.VerifyTestsByDefault;

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

    /// <summary>Extra syntax trees (e.g. synthesized global usings) added to compilation only.</summary>
    public virtual IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
        => Array.Empty<SyntaxTree>();

    // ---- hooks ----

    /// <summary>Called once after the whole-tree compilation is built, before file rewrites.</summary>
    public virtual Task CompilationReadyAsync(CSharpCompilation compilation)
        => Task.CompletedTask;

    /// <summary>Rewrite one file; return null/Changed=false to keep the file verbatim.</summary>
    public abstract Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context);

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
