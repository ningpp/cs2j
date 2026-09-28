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
            Path.GetFullPath(request.DestinationRoot), descriptor.IntermediateDirectoryName);
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

    public static async Task<PassTreeContext> PrepareTreeAsync(
        ProjectPassAdapter adapter,
        ProjectPassRunRequest request)
    {
        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);
        var intermediateRoot = Path.Combine(destinationRoot, adapter.Descriptor.IntermediateDirectoryName);

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

        var sourceRoot = layout.SourceRoot;
        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, adapter.Verbose);
        var exclusionInfo = adapter.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph, adapter.Verbose, adapter.CompatNamespacePrefixes, layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot, intermediateRoot, adapter.LogPrefix, adapter.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot, destinationRoot)
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new PassTreeContext
        {
            Layout = layout,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            DestinationRoot = destinationRoot,
            ProjectGraph = projectGraph,
            Exclusion = exclusionInfo,
            SourceFiles = sourceFiles,
            CsFiles = csFiles,
        };
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

                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            SyntaxTree? syntaxTree = compilation?.SyntaxTrees.FirstOrDefault(tree =>
                string.Equals(tree.FilePath, sourceFile, StringComparison.OrdinalIgnoreCase));

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
                File.Copy(sourceFile, destinationFile, overwrite: true);
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
