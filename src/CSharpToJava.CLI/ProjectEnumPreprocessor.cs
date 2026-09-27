using CSharpToJava.Core.EnumPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI;

internal sealed class ProjectEnumPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public EnumPreprocessorOptions Options { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
    public bool Strict { get; init; } = true;
}

internal sealed class ProjectEnumPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required EnumPreprocessorStatistics Statistics { get; init; }
    public required IReadOnlyList<EnumPreprocessorDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectEnumPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-enum-src";

    public static async Task<ProjectEnumPreprocessResult> PreprocessAsync(
        ProjectEnumPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(
            Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Enum preprocess source directory already exists: {intermediateRoot}. " +
                    "Use --force to overwrite.");
            }
            Directory.Delete(intermediateRoot, recursive: true);
        }
        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<EnumPreprocessorDiagnostic>();
        var statistics = new EnumPreprocessorStatistics();
        var rewriter = new EnumPreprocessor();
        var sourceRoot = layout.SourceRoot;
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph, request.Verbose,
                request.CompatNamespacePrefixes, layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot, intermediateRoot, "[enum]", request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot,
                Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Pre-parse all .cs files for SemanticModel
        var parseOptions = new CSharpParseOptions(
            preprocessorSymbols: new[] { "NETCOREAPP" });
        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in csFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions, path: csFile);
            syntaxTrees[csFile] = tree;
        }

        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        var compilation = CSharpCompilation.Create(
            "enum-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        // Process each .cs file
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    continue;
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
            var result = rewriter.Preprocess(sourceCode, semanticModel, request.Options);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with { FilePath = relativePath });
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(destinationFile, result.OutputCode,
                    new System.Text.UTF8Encoding(false));
            }

            if (request.Verbose)
            {
                var status = result.Changed ? "transformed"
                    : result.Statistics.FilesSkippedClean > 0 ? "clean (copy)"
                    : "dirty-no-rewrite (copy)";
                Console.WriteLine($"[enum] {status}: {relativePath}");
            }
        }

        // Residual detection (accumulated from per-file post-checks)
        if (statistics.ResidualCount > 0)
        {
            var severity = request.Strict
                ? EnumPreprocessorSeverity.Error
                : EnumPreprocessorSeverity.Warning;
            diagnostics.Add(new EnumPreprocessorDiagnostic(
                severity,
                $"{statistics.ResidualCount} residual enum bitwise/HasFlag node(s) remaining after preprocessing."));
        }

        // Restore + verify build/tests
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[enum]");
            if (request.Verbose)
                Console.WriteLine($"Restore exit code: {restoreExitCode}");

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new EnumPreprocessorDiagnostic(
                    EnumPreprocessorSeverity.Warning,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}."));
            }

            await VerifyBuildAndTestsAsync(restoreTarget, projectGraph, exclusionInfo,
                request, diagnostics);
            DeleteBuildOutputDirectories(intermediateRoot);
        }

        return new ProjectEnumPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == EnumPreprocessorSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static async Task VerifyBuildAndTestsAsync(
        string restoreTarget,
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        ProjectEnumPreprocessRequest request,
        List<EnumPreprocessorDiagnostic> diagnostics)
    {
        if (!request.VerifyBuild && !request.VerifyTests)
            return;

        Console.WriteLine($"[enum] Verifying build: dotnet build \"{restoreTarget}\" -c Release");
        var buildExit = await RunDotNetCommandAsync("build", restoreTarget, request.Verbose, "Release");
        if (buildExit != 0)
        {
            diagnostics.Add(new EnumPreprocessorDiagnostic(
                EnumPreprocessorSeverity.Error,
                $"dotnet build failed with exit code {buildExit} after enum preprocessing."));
            return;
        }

        if (request.VerifyTests && HasSupportedTestProjects(projectGraph, exclusionInfo))
        {
            Console.WriteLine($"[enum] Verifying tests: dotnet test \"{restoreTarget}\" -c Release");
            var testExit = await RunDotNetCommandAsync("test", restoreTarget, request.Verbose, "Release");
            if (testExit != 0)
            {
                diagnostics.Add(new EnumPreprocessorDiagnostic(
                    EnumPreprocessorSeverity.Error,
                    $"dotnet test failed with exit code {testExit} after enum preprocessing."));
            }
        }
    }

    private static bool HasSupportedTestProjects(
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        if (projectGraph == null)
            return false;
        return projectGraph.ProjectsInTopologicalOrder.Any(project =>
            project.Kind == ProjectKind.Test &&
            !exclusionInfo.ExcludedProjectPaths.Contains(Path.GetFullPath(project.ProjectFilePath)));
    }

    private static async Task ForwardOutputAsync(System.IO.StreamReader source, TextWriter destination)
    {
        string? line;
        while ((line = await source.ReadLineAsync()) != null)
            await destination.WriteLineAsync(line);
    }

    private static async Task<int> RunDotNetCommandAsync(
        string verb, string targetPath, bool verbose, string? configuration = null)
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
            try { Directory.Delete(binDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void MergeStatistics(
        EnumPreprocessorStatistics target,
        EnumPreprocessorStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.FilesDirtyNoRewrite += source.FilesDirtyNoRewrite;
        target.BitwiseOrRewritten += source.BitwiseOrRewritten;
        target.BitwiseAndRewritten += source.BitwiseAndRewritten;
        target.BitwiseXorRewritten += source.BitwiseXorRewritten;
        target.BitwiseNotRewritten += source.BitwiseNotRewritten;
        target.CompoundAssignRewritten += source.CompoundAssignRewritten;
        target.HasFlagRewritten += source.HasFlagRewritten;
        target.NullableLiftedRewritten += source.NullableLiftedRewritten;
        target.SkippedUnsupported += source.SkippedUnsupported;
        target.ResidualCount += source.ResidualCount;
    }
}
