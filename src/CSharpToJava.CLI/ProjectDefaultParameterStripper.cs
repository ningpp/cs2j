using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI;

internal sealed class ProjectDefaultParameterStripRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public DefaultParameterStripperOptions RewriterOptions { get; init; } = new();

    /// <summary>
    /// When true (default) the preprocessor first scans the project graph and only
    /// copies/transforms projects the converter supports, rewriting the copied .sln so
    /// unsupported (e.g. WPF/UWP/WinForms) projects are dropped.
    /// </summary>
    public bool FilterUnsupportedProjects { get; init; } = true;

    /// <summary>
    /// Explicit namespace mappings from the type-mapping config (e.g.
    /// System.Windows.Forms → dotnet.winforms.swing). Windows-only projects whose
    /// platform namespaces are covered are kept instead of excluded.
    /// </summary>
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }

    /// <summary>Run <c>dotnet build</c> on the strip-default output to verify it compiles.</summary>
    public bool VerifyBuild { get; init; } = true;

    /// <summary>Run <c>dotnet test</c> on the strip-default output (only when test projects exist).</summary>
    public bool VerifyTests { get; init; } = true;
}

internal sealed class ProjectDefaultParameterStripResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required DefaultParameterStripperStatistics Statistics { get; init; }
    public required IReadOnlyList<DefaultParameterStripDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectDefaultParameterStripper
{
    public const string IntermediateDirectoryName = ".cs2j-strip-default-src";

    public static async Task<ProjectDefaultParameterStripResult> PreprocessAsync(ProjectDefaultParameterStripRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Strip-default source directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<DefaultParameterStripDiagnostic>();
        var statistics = new DefaultParameterStripperStatistics();
        var preprocessor = new DefaultParameterStripper();
        var sourceRoot = layout.SourceRoot;
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);

        // Strategy: before transforming, scan the project graph to determine which
        // projects the converter supports, then copy ONLY those projects (preserving the
        // directory structure) and rewrite the copied .sln to drop the unsupported ones.
        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph,
                request.Verbose,
                request.CompatNamespacePrefixes,
                layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        // Copy project files (.csproj, .sln) to the intermediate directory so that
        // SolutionLoader can discover the full project graph after preprocessing.
        await ProjectPreprocessSource.CopyProjectFilesAsync(layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot,
            intermediateRoot,
            "[strip-default]",
            request.Verbose);

        // Collect all .cs files and parse them into syntax trees for compilation.
        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot,
                intermediateRoot,
                Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Parse all C# files into syntax trees
        var parseOptions = new CSharpParseOptions(
            preprocessorSymbols: new[] { "NETCOREAPP" });
        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in csFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions, path: csFile);
            syntaxTrees[csFile] = tree;
        }

        // Create a compilation with all trees for semantic analysis (so the rewriter
        // can distinguish type parameters from named types via the SemanticModel).
        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        var compilation = CSharpCompilation.Create(
            "strip-default-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        // Pass 1: Collect existing signatures for cross-file conflict detection
        foreach (var kvp in syntaxTrees)
        {
            var model = compilation.GetSemanticModel(kvp.Value);
            preprocessor.Collect(kvp.Value, model);
        }

        // Pass 2: Process each .cs file — strip defaults + generate overloads
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
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

            if (!syntaxTrees.TryGetValue(sourceFile, out var tree))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var semanticModel = compilation.GetSemanticModel(tree);
            var result = preprocessor.Strip(sourceCode, semanticModel, request.RewriterOptions);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(destinationFile, result.OutputCode, new System.Text.UTF8Encoding(false));

                // Pass 3: Verify no residual defaults remain
                var verifyErrors = DefaultParameterVerifier.Verify(result.OutputCode);
                foreach (var error in verifyErrors)
                {
                    diagnostics.Add(new DefaultParameterStripDiagnostic(
                        error.Severity,
                        $"{relativePath}: {error.Message}"));
                }
            }

            if (request.Verbose)
            {
                Console.WriteLine($"[strip-default] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        // Restore NuGet packages in the intermediate directory so that
        // MSBuildWorkspace can resolve package references when it opens the
        // preprocessed project.
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
            {
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            }
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[strip-default]");
            if (request.Verbose)
            {
                Console.WriteLine($"Restore exit code: {restoreExitCode}");
            }
            if (restoreExitCode != 0)
            {
                diagnostics.Add(new DefaultParameterStripDiagnostic(
                    DefaultParameterStripSeverity.Warning,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}. " +
                    "Package references may not be resolved, leading to incorrect code generation."));
            }

            // Verify that the strip-default-preprocessed C# still builds (and its tests pass).
            await VerifyBuildAndTestsAsync(restoreTarget, projectGraph, exclusionInfo, request, diagnostics);
            DeleteBuildOutputDirectories(intermediateRoot);
        }

        return new ProjectDefaultParameterStripResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == DefaultParameterStripSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static async Task VerifyBuildAndTestsAsync(
        string restoreTarget,
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        ProjectDefaultParameterStripRequest request,
        List<DefaultParameterStripDiagnostic> diagnostics)
    {
        if (!request.VerifyBuild && !request.VerifyTests)
        {
            return;
        }

        Console.WriteLine($"[strip-default] Verifying build: dotnet build \"{restoreTarget}\" -c Release");
        var buildExit = await RunDotNetCommandAsync("build", restoreTarget, request.Verbose, "Release");
        if (buildExit != 0)
        {
            diagnostics.Add(new DefaultParameterStripDiagnostic(
                DefaultParameterStripSeverity.Error,
                $"dotnet build failed with exit code {buildExit} after strip-default preprocessing. " +
                "The strip-default rewrite produced code that does not compile."));
            return;
        }

        if (request.VerifyTests && HasSupportedTestProjects(projectGraph, exclusionInfo))
        {
            Console.WriteLine($"[strip-default] Verifying tests: dotnet test \"{restoreTarget}\" -c Release");
            var testExit = await RunDotNetCommandAsync("test", restoreTarget, request.Verbose, "Release");
            if (testExit != 0)
            {
                diagnostics.Add(new DefaultParameterStripDiagnostic(
                    DefaultParameterStripSeverity.Error,
                    $"dotnet test failed with exit code {testExit} after strip-default preprocessing."));
            }
        }
    }

    private static bool HasSupportedTestProjects(
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

    private static async Task ForwardOutputAsync(System.IO.StreamReader source, TextWriter destination)
    {
        string? line;
        while ((line = await source.ReadLineAsync()) != null)
        {
            await destination.WriteLineAsync(line);
        }
    }

    private static async Task<int> RunDotNetCommandAsync(string verb, string targetPath, bool verbose, string? configuration = null)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"{verb} \"{targetPath}\" --nologo" +
                            (configuration is null ? "" : $" -c {configuration}") +
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

            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"dotnet {verb} exited with code {process.ExitCode} for {targetPath}");
            }
            else
            {
                Console.WriteLine($"[strip-default] dotnet {verb} succeeded for {targetPath}");
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotnet {verb} failed: {ex.Message}");
            return -1;
        }
    }

    private static void DeleteBuildOutputDirectories(string root)
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

    private static void MergeStatistics(DefaultParameterStripperStatistics target, DefaultParameterStripperStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.DeclarationsStripped += source.DeclarationsStripped;
        target.ParametersStripped += source.ParametersStripped;
        target.OverloadsGenerated += source.OverloadsGenerated;
        target.SkippedNoBody += source.SkippedNoBody;
        target.SkippedConflict += source.SkippedConflict;
        target.SkippedHidesAbstract += source.SkippedHidesAbstract;
        target.SkippedUnsupported += source.SkippedUnsupported;
    }
}
