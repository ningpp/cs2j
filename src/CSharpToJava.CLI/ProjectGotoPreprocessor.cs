using System.Diagnostics;
using CSharpToJava.Core.GotoEliminator;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI;

internal sealed class ProjectGotoPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public bool Strict { get; init; }
}

internal sealed class ProjectGotoPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required GotoEliminatorStatistics Statistics { get; init; }
    public required IReadOnlyList<GotoEliminatorDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectGotoPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-no-goto-src";

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "node_modules",
    };

    public static async Task<ProjectGotoPreprocessResult> PreprocessAsync(ProjectGotoPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ResolveSourceLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"No-goto source directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<GotoEliminatorDiagnostic>();
        var statistics = new GotoEliminatorStatistics();
        var eliminator = new GotoEliminator();
        var options = new GotoEliminatorOptions(request.Verbose, request.Strict);
        var sourceRoot = layout.SourceRoot;
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);

        // Copy project files (.csproj, .sln) to the intermediate directory so that
        // SolutionLoader can discover the full project graph after preprocessing.
        foreach (var projectFile in layout.ProjectFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, projectFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(projectFile, destinationFile, overwrite: true);
        }

        foreach (var sourceFile in EnumerateSourceFiles(sourceRoot, destinationRoot))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var result = eliminator.Eliminate(sourceCode, options);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Message = $"{relativePath}: {diagnostic.Message}",
                });
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(destinationFile, result.OutputCode, new System.Text.UTF8Encoding(false));
            }

            var outputCode = result.Changed ? result.OutputCode : sourceCode;
            if (ContainsGotoOrLabel(outputCode))
            {
                diagnostics.Add(new GotoEliminatorDiagnostic(
                    GotoEliminatorSeverity.Error,
                    $"{relativePath}: goto/label syntax remains after preprocessing"));
            }
        }

        // Restore NuGet packages in the intermediate directory so that
        // MSBuildWorkspace can resolve package references when it opens the
        // preprocessed project. Without a restore, MetadataReferences is empty
        // and the semantic model is broken, which leads to incorrect code
        // generation (e.g. property accesses emitted as field accesses).
        var restoreTarget = ResolveRestoreTarget(intermediateRoot, layout);
        if (restoreTarget != null)
        {
            if (request.Verbose)
            {
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            }
            var restoreExitCode = RunDotNetRestore(restoreTarget);
            if (request.Verbose)
            {
                Console.WriteLine($"Restore exit code: {restoreExitCode}");
            }
            if (restoreExitCode != 0)
            {
                diagnostics.Add(new GotoEliminatorDiagnostic(
                    GotoEliminatorSeverity.Error,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}. " +
                    "Package references may not be resolved, leading to incorrect code generation."));
            }
        }

        return new ProjectGotoPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = MapToIntermediate(layout, intermediateRoot),
            Success = diagnostics.Count == 0,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static IEnumerable<string> EnumerateSourceFiles(string sourceRoot, string destinationRoot)
    {
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            if (IsSameOrDescendantPath(currentDirectory, destinationRoot))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (IgnoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                {
                    continue;
                }

                if (IsSameOrDescendantPath(subDirectory, destinationRoot))
                {
                    continue;
                }

                pending.Push(Path.GetFullPath(subDirectory));
            }
        }
    }

    private static ProjectGotoSourceLayout ResolveSourceLayout(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);

        // Case 1: Source is a .sln file
        if (File.Exists(fullSourcePath) && Path.GetExtension(fullSourcePath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(
                graph.ProjectsInTopologicalOrder
                    .Select(project => project.ProjectDirectory));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .Append(fullSourcePath)
                .ToList();
            return new ProjectGotoSourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        // Case 2: Source is a .csproj file
        if (File.Exists(fullSourcePath) && Path.GetExtension(fullSourcePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(
                graph.ProjectsInTopologicalOrder
                    .Select(project => project.ProjectDirectory));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .ToList();
            return new ProjectGotoSourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        // Case 3: Source is a directory
        if (Directory.Exists(fullSourcePath))
        {
            // Try to find a project entry point (.sln or .csproj) in the directory
            if (ProjectDiscovery.TryResolveProjectEntry(fullSourcePath, out var entryProjectPath))
            {
                var graph = ProjectDiscovery.LoadProjectGraph(entryProjectPath);
                var sourceRoot = GetCommonRoot(
                    graph.ProjectsInTopologicalOrder
                        .Select(project => project.ProjectDirectory));

                // Collect all project files and the solution file (if any)
                var projectFiles = graph.ProjectsInTopologicalOrder
                    .Select(p => p.ProjectFilePath)
                    .ToList();

                // Also include the .sln file if it was resolved
                if (Path.GetExtension(entryProjectPath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    projectFiles.Add(entryProjectPath);
                }
                else
                {
                    // Search for .sln files in the common root and the entry directory
                    // so SolutionLoader can discover the full project graph from the
                    // intermediate directory.
                    var slnDirs = new[] { sourceRoot, fullSourcePath };
                    foreach (var searchDir in slnDirs)
                    {
                        if (!Directory.Exists(searchDir)) continue;
                        var slnInDir = Directory.GetFiles(searchDir, "*.sln", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault();
                        if (slnInDir != null)
                        {
                            projectFiles.Add(slnInDir);
                            break;
                        }
                    }
                }

                return new ProjectGotoSourceLayout(sourceRoot, fullSourcePath, projectFiles);
            }

            // No project file found — fall back to directory-only mode
            return new ProjectGotoSourceLayout(fullSourcePath, fullSourcePath, Array.Empty<string>());
        }

        throw new FileNotFoundException("Source path not found.", fullSourcePath);
    }

    private static string MapToIntermediate(ProjectGotoSourceLayout layout, string intermediateRoot)
    {
        // When the source is a directory (e.g., a folder within a larger solution),
        // return the intermediate root so that SolutionLoader can discover the .sln
        // and load all projects (including transitive project references like MSAGL).
        if (layout.ProjectFiles.Count > 0 && Directory.Exists(layout.EntryPath))
        {
            return intermediateRoot;
        }

        // When the source is a single project file (.csproj), map directly to the
        // preprocessed project file. There is no .sln to discover at the intermediate
        // root, so returning the root would make MSBuild and ProjectDiscovery fail to
        // resolve the project (its .csproj lives in a subdirectory), silently falling
        // back to single-module mode. Mapping to the project file lets MSBuild follow
        // its ProjectReferences and produce a proper multi-module layout.
        if (File.Exists(layout.EntryPath))
        {
            return Path.Combine(intermediateRoot, Path.GetRelativePath(layout.SourceRoot, layout.EntryPath));
        }

        if (string.Equals(layout.SourceRoot, layout.EntryPath, StringComparison.OrdinalIgnoreCase))
        {
            return intermediateRoot;
        }

        return Path.Combine(intermediateRoot, Path.GetRelativePath(layout.SourceRoot, layout.EntryPath));
    }

    internal static string? ResolveRestoreTarget(string intermediateRoot, ProjectGotoSourceLayout layout)
    {
        // If the intermediate path maps to a specific .sln or .csproj file, restore that.
        var preprocessedPath = MapToIntermediate(layout, intermediateRoot);
        if (File.Exists(preprocessedPath) &&
            (preprocessedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
             preprocessedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return preprocessedPath;
        }

        // Otherwise the intermediate path is a directory: look for the entry solution/project there.
        if (Directory.Exists(preprocessedPath))
        {
            var sln = Directory.GetFiles(preprocessedPath, "*.sln", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (sln != null) return sln;

            var csproj = Directory.GetFiles(preprocessedPath, "*.csproj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (csproj != null) return csproj;
        }

        return null;
    }

    private static int RunDotNetRestore(string targetPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"restore \"{targetPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine("Failed to start dotnet restore process.");
                return -1;
            }
            // Read stdout/stderr asynchronously before WaitForExit to avoid deadlock
            // when the internal pipe buffer fills and the process blocks on write.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var output = outputTask.Result;
                var error = errorTask.Result;
                Console.Error.WriteLine($"dotnet restore exited with code {process.ExitCode} for {targetPath}");
                if (!string.IsNullOrWhiteSpace(output))
                    Console.Error.WriteLine($"stdout: {output}");
                if (!string.IsNullOrWhiteSpace(error))
                    Console.Error.WriteLine($"stderr: {error}");
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotnet restore failed: {ex.Message}");
            return -1;
        }
    }

    private static bool ContainsGotoOrLabel(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().Any(node => node is GotoStatementSyntax or LabeledStatementSyntax);
    }

    private static void MergeStatistics(GotoEliminatorStatistics target, GotoEliminatorStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.MethodsTransformed += source.MethodsTransformed;
        target.MethodsSkipped += source.MethodsSkipped;
        target.GotosEliminated += source.GotosEliminated;
    }

    private static string GetCommonRoot(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            return Path.GetFullPath(".");
        }

        var commonRoot = normalized[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (normalized.Any(path => !IsSameOrDescendantPath(path, commonRoot)))
        {
            var parent = Directory.GetParent(commonRoot);
            if (parent == null)
            {
                break;
            }

            commonRoot = parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return commonRoot;
    }

    private static bool IsSameOrDescendantPath(string path, string candidateAncestor)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullAncestor = Path.GetFullPath(candidateAncestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullPath, fullAncestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullAncestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record ProjectGotoSourceLayout(string SourceRoot, string EntryPath, IReadOnlyList<string> ProjectFiles);
}
