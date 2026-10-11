using System.Text;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Common execution engine for every project-level preprocess pass. Owns the shared
/// pipeline that used to be copy-pasted across the fifteen Project*Preprocessor
/// facades:
/// resolve layout → intermediate directory → project filtering → tree copy →
/// parse/compile → per-file rewrite → after-files hooks → restore → verify → cleanup.
/// Custom multi-phase passes call the public stage helpers directly.
/// </summary>
internal static class ProjectPassRunner
{
    /// <summary>
    /// Shared intermediate directory used by every pass when
    /// <see cref="ProjectPassRunRequest.UseUnifiedIntermediateDirectory"/> is set.
    /// Passes rewrite this single tree in place instead of producing one full
    /// tree copy per pass.
    /// </summary>
    public const string UnifiedIntermediateDirectoryName = ".cs2j-preprocessed-src";

    /// <summary>Run a fully implemented pass adapter over a project tree.</summary>
    public static async Task<ProjectPassRunResult> RunAsync(
        ProjectPassAdapter adapter,
        ProjectPassRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var tree = await PrepareTreeAsync(adapter, request);
        var context = new PassExecutionContext
        {
            Request = request,
            Adapter = adapter,
            Tree = tree,
            Success = true,
        };

        await adapter.ExecuteAsync(context);

        var success = context.Success && !adapter.HasErrors;
        return new ProjectPassRunResult
        {
            OriginalSourcePath = tree.Layout.EntryPath,
            SourceRoot = tree.SourceRoot,
            IntermediateRoot = tree.IntermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(
                tree.Layout, tree.IntermediateRoot),
            Success = success,
            StubRejected = false,
        };
    }

    /// <summary>
    /// Reject an explicitly enabled stub with CS2J9001 before touching the filesystem.
    /// </summary>
    public static ProjectPassRunResult RunRejectedStub(
        PassDescriptor descriptor,
        ProjectPassRunRequest request)
    {
        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(
            Path.GetFullPath(request.DestinationRoot), ResolveIntermediateDirectoryName(request, descriptor));
        return new ProjectPassRunResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = layout.SourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = false,
            StubRejected = true,
        };
    }

    public static string StubMessage(PassDescriptor descriptor)
        => $"{PreprocessDiagnosticCodes.StubNotImplemented}: pass '{descriptor.Id}' is a registered " +
           $"placeholder and is not implemented yet.";

    // ---- stage 1: tree preparation ----

    /// <summary>
    /// Physical intermediate directory name for a run: the shared unified
    /// directory when the request opted in, otherwise the pass' own name.
    /// </summary>
    public static string ResolveIntermediateDirectoryName(
        ProjectPassRunRequest request,
        PassDescriptor descriptor)
        => request.UseUnifiedIntermediateDirectory
            ? UnifiedIntermediateDirectoryName
            : descriptor.IntermediateDirectoryName;

    /// <summary>
    /// True when the pass input tree already IS the intermediate tree: a later
    /// pass in a unified-directory chain must rewrite in place instead of
    /// deleting/copying the tree.
    /// </summary>
    private static bool IsInPlaceTree(string sourceRoot, string intermediateRoot)
        => string.Equals(
            Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(intermediateRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    public static async Task<PassTreeContext> PrepareTreeAsync(
        ProjectPassAdapter adapter,
        ProjectPassRunRequest request)
    {
        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);
        var intermediateRoot = Path.Combine(
            destinationRoot, ResolveIntermediateDirectoryName(request, adapter.Descriptor));

        // In a unified chain the 2nd+ pass reads from and writes to the same
        // tree it prepared on the first pass: never delete or re-copy it.
        var inPlace = IsInPlaceTree(layout.SourceRoot, intermediateRoot);

        if (!inPlace)
        {
            if (Directory.Exists(intermediateRoot))
            {
                if (!request.Force)
                {
                    throw new InvalidOperationException(
                        $"{adapter.Descriptor.DisplayName} preprocess source directory already exists: " +
                        $"{intermediateRoot}. Use --force to overwrite.");
                }

                Directory.Delete(intermediateRoot, recursive: true);
            }

            Directory.CreateDirectory(intermediateRoot);
        }
        else if (!Directory.Exists(intermediateRoot))
        {
            throw new DirectoryNotFoundException(
                $"{adapter.Descriptor.DisplayName} unified preprocess source directory is missing: " +
                intermediateRoot);
        }

        var sourceRoot = layout.SourceRoot;
        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, adapter.Verbose);
        var exclusionInfo = adapter.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph, adapter.Verbose, adapter.CompatNamespacePrefixes, layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot, destinationRoot)
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        // Keep only files that belong to a project of the loaded entry graph
        // (plus the entry files themselves). Without this, unrelated source
        // trees sharing the source root (e.g. a sibling repo not referenced by
        // the .sln) are compiled with the pass' basic metadata set and fail
        // type resolution, surfacing as spurious strict-gate errors.
        sourceFiles = ProjectPreprocessSource.FilterToGraphMembers(
            sourceFiles, projectGraph, layout, exclusionInfo);
        var csFiles = sourceFiles
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tree = new PassTreeContext
        {
            Layout = layout,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            DestinationRoot = destinationRoot,
            ProjectGraph = projectGraph,
            Exclusion = exclusionInfo,
            SourceFiles = sourceFiles,
            CsFiles = csFiles,
            InPlace = inPlace,
            Snapshot = new InPlaceSourceSnapshot(inPlace),
        };

        // Strict syntax gate: a project file containing parse errors must abort
        // the load immediately instead of flowing into the tolerant Roslyn
        // compilation/rewrite pipeline. Runs before the intermediate tree is
        // populated so a rejected load leaves no copied sources behind.
        ValidateSyntaxOrThrow(adapter, tree);

        if (!inPlace)
        {
            await ProjectPreprocessSource.CopyProjectFilesAsync(
                layout, sourceRoot, intermediateRoot, exclusionInfo);
            ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
                sourceRoot, intermediateRoot, adapter.LogPrefix, adapter.Verbose);
        }

        return tree;
    }

    /// <summary>
    /// Pre-parses every project .cs file with the adapter's parse options and
    /// throws <see cref="ProjectSyntaxException"/> on the first Error-level
    /// parse diagnostic. SyntaxTree diagnostics are parser-only; semantic
    /// diagnostics and warnings are intentionally not gated here.
    /// </summary>
    private static void ValidateSyntaxOrThrow(ProjectPassAdapter adapter, PassTreeContext tree)
    {
        var parseOptions = adapter.CreateParseOptions(tree);
        foreach (var csFile in tree.CsFiles)
        {
            var sourceCode = File.ReadAllText(csFile);
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions, path: csFile);
            var error = syntaxTree.GetDiagnostics()
                .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            if (error == null)
            {
                continue;
            }

            var position = error.Location.IsInSource
                ? error.Location.GetLineSpan().StartLinePosition
                : new Microsoft.CodeAnalysis.Text.LinePosition(0, 0);
            throw new ProjectSyntaxException(
                adapter.LogPrefix,
                csFile,
                position.Line + 1,
                position.Character + 1,
                error.Descriptor.Id,
                error.GetMessage());
        }
    }

    // ---- stage 2: whole-tree parse + compile ----

    public static CSharpCompilation BuildStandardCompilation(
        ProjectPassAdapter adapter,
        PassTreeContext tree)
    {
        var parseOptions = adapter.CreateParseOptions(tree);
        var trees = new List<SyntaxTree>();
        foreach (var csFile in tree.CsFiles)
        {
            var sourceCode = File.ReadAllText(csFile);
            trees.Add(CSharpSyntaxTree.ParseText(sourceCode, parseOptions, path: csFile));
        }

        trees.AddRange(adapter.CreateAdditionalSyntaxTrees(tree));

        return CSharpCompilation.Create(
            adapter.CompilationName,
            trees,
            adapter.GetMetadataReferences(),
            adapter.CreateCompilationOptions());
    }

    // ---- stage 3: per-file spine ----

    public static async Task RunStandardSpineAsync(PassExecutionContext context)
    {
        var adapter = context.Adapter;
        var tree = context.Tree;

        CSharpCompilation? compilation = null;
        if (adapter.Descriptor.NeedsSemanticModel)
        {
            await adapter.OnTreePreparedAsync(tree);
            compilation = BuildStandardCompilation(adapter, tree);
            await adapter.CompilationReadyAsync(compilation);
        }
        else
        {
            await adapter.OnTreePreparedAsync(tree);
        }

        foreach (var sourceFile in tree.SourceFiles)
        {
            var relativePath = Path.GetRelativePath(tree.SourceRoot, sourceFile);
            var destinationFile = Path.Combine(tree.IntermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // In a unified-directory chain the tree is rewritten in place,
                // so source and destination are the same file.
                if (!string.Equals(
                        Path.GetFullPath(sourceFile),
                        Path.GetFullPath(destinationFile),
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceFile, destinationFile, overwrite: true);
                }

                continue;
            }

            SyntaxTree? syntaxTree = compilation?.SyntaxTrees.FirstOrDefault(tree =>
                string.Equals(tree.FilePath, sourceFile, StringComparison.OrdinalIgnoreCase));

            // Capture pristine bytes before any rewrite so in-place chains can
            // roll a file back after the fact (e.g. extension-methods build gate).
            tree.Snapshot.Capture(sourceFile);

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var fileContext = new PassFileContext
            {
                SourceCode = sourceCode,
                SourceFilePath = sourceFile,
                RelativePath = relativePath,
                DestinationFilePath = destinationFile,
                SemanticModel = syntaxTree == null || compilation == null
                    ? null
                    : compilation.GetSemanticModel(syntaxTree),
            };

            var rewrite = await adapter.RewriteFileAsync(fileContext);
            if (rewrite is not { Changed: true })
            {
                // Unchanged file: copy it over unless the pass is rewriting the
                // tree in place (unified directory), where paths are identical.
                if (!string.Equals(
                        Path.GetFullPath(sourceFile),
                        Path.GetFullPath(destinationFile),
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceFile, destinationFile, overwrite: true);
                }

                continue;
            }

            await WriteRewrittenAsync(tree, fileContext, rewrite);
        }

        await adapter.AfterFilesAsync(tree);

        // ---- stage 4: restore ----

        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(
            tree.Layout, tree.IntermediateRoot);
        if (restoreTarget != null)
        {
            if (adapter.Verbose)
            {
                Console.WriteLine($"{adapter.LogPrefix} Restoring packages: {restoreTarget}");
            }

            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(
                restoreTarget, adapter.LogPrefix);

            if (adapter.Verbose)
            {
                Console.WriteLine($"{adapter.LogPrefix} Restore exit code: {restoreExitCode}");
            }

            if (restoreExitCode != 0)
            {
                adapter.OnRestoreFailed(restoreExitCode, restoreTarget);
            }

            // ---- stage 5: verify build/tests + cleanup ----

            await VerifyBuildAndTestsAsync(adapter, tree, restoreTarget);
            if (adapter.DeleteBuildOutputsAfterRestore)
            {
                DeleteBuildOutputs(tree.IntermediateRoot);
            }
        }
    }

    public static void CopyVerbatim(PassTreeContext tree, string sourceFilePath, string relativePath)
    {
        var destinationFile = Path.Combine(tree.IntermediateRoot, relativePath);
        if (string.Equals(
                Path.GetFullPath(sourceFilePath),
                Path.GetFullPath(destinationFile),
                StringComparison.OrdinalIgnoreCase))
        {
            // In-place unified tree: the file is already where it needs to be.
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        File.Copy(sourceFilePath, destinationFile, overwrite: true);
    }

    public static ValueTask WriteRewrittenAsync(
        PassTreeContext tree,
        PassFileContext context,
        PassFileRewrite rewrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(context.DestinationFilePath)!);
        var useBom = rewrite.PreserveBom && FileStartsWithBom(context.SourceFilePath);
        return new ValueTask(File.WriteAllTextAsync(
            context.DestinationFilePath, rewrite.OutputCode, new UTF8Encoding(useBom)));
    }

    // ---- stage 4: restore (standalone helper for custom executions) ----

    public static int Restore(ProjectPassAdapter adapter, PassTreeContext tree)
    {
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(
            tree.Layout, tree.IntermediateRoot);
        if (restoreTarget == null)
        {
            return 0;
        }

        if (adapter.Verbose)
        {
            Console.WriteLine($"{adapter.LogPrefix} Restoring packages: {restoreTarget}");
        }

        var exitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, adapter.LogPrefix);
        if (exitCode != 0)
        {
            adapter.OnRestoreFailed(exitCode, restoreTarget);
        }

        return exitCode;
    }

    // ---- stage 5: build/test verification ----

    public static async Task<bool> VerifyBuildAndTestsAsync(
        ProjectPassAdapter adapter,
        PassTreeContext tree,
        string? restoreTargetOverride = null)
    {
        if (!adapter.VerifyBuild && !adapter.VerifyTests)
        {
            return true;
        }

        var restoreTarget = restoreTargetOverride
            ?? ProjectPreprocessSource.ResolveRestoreTarget(tree.Layout, tree.IntermediateRoot);
        if (restoreTarget == null)
        {
            return true;
        }

        if (adapter.StreamVerifyOutput)
        {
            Console.WriteLine(
                $"{adapter.LogPrefix} Verifying build: dotnet build \"{restoreTarget}\" -c Release");
        }

        if (adapter.StreamVerifyOutput)
        {
            var buildExit = await RunDotNetCommandAsync(adapter, "build", restoreTarget);
            if (buildExit != 0)
            {
                adapter.OnBuildFailed(buildExit);
                return false;
            }
        }
        else
        {
            var buildExit = await RunDotNetCapturedAsync(adapter, "build", restoreTarget);
            adapter.ReportVerifyOutcome("build", buildExit.ExitCode, buildExit.Output);
            if (buildExit.ExitCode != 0)
            {
                return false;
            }
        }

        if (adapter.VerifyTests && HasSupportedTestProjects(tree.ProjectGraph, tree.Exclusion))
        {
            if (adapter.StreamVerifyOutput)
            {
                Console.WriteLine(
                    $"{adapter.LogPrefix} Verifying tests: dotnet test \"{restoreTarget}\" -c Release");
                var testExit = await RunDotNetCommandAsync(adapter, "test", restoreTarget);
                if (testExit != 0)
                {
                    adapter.OnTestsFailed(testExit);
                    return false;
                }
            }
            else
            {
                var testExit = await RunDotNetCapturedAsync(adapter, "test", restoreTarget);
                adapter.ReportVerifyOutcome("test", testExit.ExitCode, testExit.Output);
                if (testExit.ExitCode != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static bool HasSupportedTestProjects(
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        if (projectGraph == null)
        {
            return false;
        }

        return projectGraph.ProjectsInTopologicalOrder.Any(project =>
            project.Kind == ProjectKind.Test &&
            !exclusionInfo.ExcludedProjectPaths.Contains(Path.GetFullPath(project.ProjectFilePath)));
    }

    /// <summary>
    /// Silent variant of <see cref="RunDotNetCommandAsync"/> capturing stdout/stderr
    /// (historical ref/out/in verification behavior). The adapter formats OK/EXIT.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunDotNetCapturedAsync(
        ProjectPassAdapter adapter,
        string verb,
        string targetPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"{verb} \"{targetPath}\" --nologo -c Release" +
                            " -p:GenerateResourceUsePreserializedResources=true",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                return (-1, string.Empty);
            }

            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return (process.ExitCode, output.ToString());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static async Task ForwardOutputAsync(StreamReader source, TextWriter destination)
    {
        string? line;
        while ((line = await source.ReadLineAsync()) != null)
        {
            await destination.WriteLineAsync(line);
        }
    }

    private static async Task<int> RunDotNetCommandAsync(
        ProjectPassAdapter adapter,
        string verb,
        string targetPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"{verb} \"{targetPath}\" --nologo -c Release" +
                            " -p:GenerateResourceUsePreserializedResources=true",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine($"Failed to start dotnet {verb} process.");
                return -1;
            }

            var outputTask = ForwardOutputAsync(process.StandardOutput, Console.Out);
            var errorTask = ForwardOutputAsync(process.StandardError, Console.Error);
            await process.WaitForExitAsync();
            await outputTask;
            await errorTask;
            adapter.ReportVerifyCommandResult(verb, targetPath, process.ExitCode);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotnet {verb} failed: {ex.Message}");
            return -1;
        }
    }

    /// <summary>Delete every bin/ directory under the intermediate tree (post-verify cleanup).</summary>
    public static void DeleteBuildOutputs(string root)
    {
        foreach (var binDirectory in Directory
                     .EnumerateDirectories(root, "bin", SearchOption.AllDirectories)
                     .ToList())
        {
            try
            {
                Directory.Delete(binDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }
}

/// <summary>Raised when a project .cs source file contains syntax errors during load.</summary>
internal sealed class ProjectSyntaxException : Exception
{
    public ProjectSyntaxException(
        string logPrefix,
        string filePath,
        int line,
        int column,
        string diagnosticId,
        string diagnosticMessage)
        : base(
            $"{logPrefix} failed to load project: syntax error in source file.\n" +
            $"{filePath}({line},{column}): error {diagnosticId}: {diagnosticMessage}")
    {
    }
}
