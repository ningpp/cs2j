using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI;

internal sealed class ProjectUnsignedTypePreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public UnsignedTypeRewriterOptions RewriterOptions { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
}

internal sealed class ProjectUnsignedTypePreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required UnsignedTypeRewriterStatistics Statistics { get; init; }
    public required IReadOnlyList<UnsignedTypeRewriterDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectUnsignedTypePreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-unsigned-src";

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "node_modules",
        "packages",
        "TestResults",
    };

    public static async Task<ProjectUnsignedTypePreprocessResult> PreprocessAsync(
        ProjectUnsignedTypePreprocessRequest request)
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
                    $"Unsigned source directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<UnsignedTypeRewriterDiagnostic>();
        var statistics = new UnsignedTypeRewriterStatistics();
        var rewriter = new UnsignedTypeRewriter();
        var sourceRoot = layout.SourceRoot;

        var projectGraph = TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(projectGraph, request.Verbose)
            : new SupportedProjectFiltering.ExclusionInfo();

        // Copy project files (.csproj, .sln), dropping excluded projects and
        // rewriting the solution file so the intermediate tree is self-consistent.
        foreach (var projectFile in layout.ProjectFiles)
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceRoot, projectFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (exclusionInfo.HasExclusions &&
                projectFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                var solutionContent = await File.ReadAllTextAsync(projectFile);
                var rewrittenSolution = SupportedProjectFiltering.RewriteSolutionContent(
                    solutionContent,
                    Path.GetDirectoryName(projectFile) ?? sourceRoot,
                    exclusionInfo);
                await File.WriteAllTextAsync(destinationFile, rewrittenSolution, new System.Text.UTF8Encoding(false));
            }
            else
            {
                File.Copy(projectFile, destinationFile, overwrite: true);
            }
        }

        CopyAncestorMsBuildDirectoryFiles(sourceRoot, intermediateRoot, request.Verbose);

        var sourceFiles = EnumerateSourceFiles(sourceRoot, intermediateRoot)
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in csFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            syntaxTrees[csFile] = CSharpSyntaxTree.ParseText(sourceCode, path: csFile);
        }

        var references = GetBasicReferences();
        var compilation = CSharpCompilation.Create(
            "unsigned-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var usedExtensions = false;
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // The solution file was already copied (and possibly rewritten) in
                // the project-file loop; copying it again would restore the original.
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
            var result = rewriter.Rewrite(sourceCode, compilation.GetSemanticModel(tree), request.RewriterOptions);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with { Message = $"{relativePath}: {diagnostic.Message}" });
            }

            usedExtensions |= result.Statistics.OperatorsRewritten > 0 || result.Statistics.CastsRewritten > 0;

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(destinationFile, result.OutputCode, new System.Text.UTF8Encoding(false));
            }
        }

        if (usedExtensions)
        {
            var extensionPath = Path.Combine(intermediateRoot, UnsignedExtensionsFileContent.FileName);
            await File.WriteAllTextAsync(extensionPath, UnsignedExtensionsFileContent.Generate(), new System.Text.UTF8Encoding(false));
            EnsureExtensionFileIncluded(intermediateRoot);
        }

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
                diagnostics.Add(new UnsignedTypeRewriterDiagnostic(
                    UnsignedTypeSeverity.Error,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}."));
            }
        }

        return new ProjectUnsignedTypePreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == UnsignedTypeSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    internal static List<MetadataReference> GetBasicReferences()
    {
        var references = new List<MetadataReference>();
        var addedSimpleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator);
        if (trustedAssemblies != null)
        {
            foreach (var assembly in trustedAssemblies)
            {
                if (File.Exists(assembly))
                {
                    references.Add(MetadataReference.CreateFromFile(assembly));
                    addedSimpleNames.Add(Path.GetFileNameWithoutExtension(assembly));
                }
            }
        }
        else
        {
            var coreAssembly = typeof(object).Assembly;
            references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
            addedSimpleNames.Add(coreAssembly.GetName().Name ?? "System.Private.CoreLib");
            var runtimeDir = Path.GetDirectoryName(coreAssembly.Location)!;
            var runtimeAssembly = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(runtimeAssembly))
            {
                references.Add(MetadataReference.CreateFromFile(runtimeAssembly));
            }
        }

        AddWindowsDesktopReferences(references, addedSimpleNames);
        return references;
    }

    private static void AddWindowsDesktopReferences(List<MetadataReference> references, HashSet<string> addedSimpleNames)
    {
        var coreRuntimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (string.IsNullOrEmpty(coreRuntimeDir))
        {
            return;
        }

        var windowsDesktopRoot = Path.GetFullPath(Path.Combine(coreRuntimeDir, "..", "..", "Microsoft.WindowsDesktop.App"));
        if (!Directory.Exists(windowsDesktopRoot))
        {
            return;
        }

        var currentVersion = Environment.Version.ToString(3);
        var versions = Directory.EnumerateDirectories(windowsDesktopRoot)
            .Select(Path.GetFileName)
            .Where(v => !string.IsNullOrEmpty(v) && Version.TryParse(v, out _))
            .OrderByDescending(v => Version.Parse(v!))
            .ToList();
        var bestVersion = versions.FirstOrDefault(v => string.Equals(v, currentVersion, StringComparison.OrdinalIgnoreCase))
                          ?? versions.FirstOrDefault();
        if (bestVersion == null)
        {
            return;
        }

        foreach (var assembly in Directory.EnumerateFiles(Path.Combine(windowsDesktopRoot, bestVersion), "*.dll"))
        {
            var simpleName = Path.GetFileNameWithoutExtension(assembly);
            if (string.IsNullOrEmpty(simpleName) || !addedSimpleNames.Add(simpleName))
            {
                continue;
            }

            try
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
            catch (IOException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }
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

    private static readonly string[] MsBuildDirectoryFileNames =
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "nuget.config",
    };

    private static void CopyAncestorMsBuildDirectoryFiles(string sourceRoot, string intermediateRoot, bool verbose)
    {
        foreach (var fileName in MsBuildDirectoryFileNames)
        {
            var current = Path.GetFullPath(sourceRoot);
            string? found = null;
            while (!string.IsNullOrEmpty(current))
            {
                var candidate = Path.Combine(current, fileName);
                if (File.Exists(candidate))
                {
                    found = candidate;
                    break;
                }

                var parent = Path.GetDirectoryName(current);
                if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }

            if (found == null)
            {
                continue;
            }

            var destination = Path.Combine(intermediateRoot, fileName);
            File.Copy(found, destination, overwrite: true);
            if (verbose)
            {
                Console.WriteLine($"[unsigned-type] Copied MSBuild directory file: {found} -> {destination}");
            }

            CopyMsBuildThisFileDirectoryReferences(found, intermediateRoot, verbose);
        }
    }

    private static void CopyMsBuildThisFileDirectoryReferences(string sourceFile, string intermediateRoot, bool verbose)
    {
        string content;
        try
        {
            content = File.ReadAllText(sourceFile);
        }
        catch (IOException)
        {
            return;
        }

        var sourceDirectory = Path.GetDirectoryName(sourceFile)!;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     content,
                     @"\$\(MSBuildThisFileDirectory\)\s*\\?([^""<>|?*\r\n]+)"))
        {
            var relativeReference = match.Groups[1].Value.Trim().TrimEnd('\\', '/');
            if (relativeReference.Length == 0) continue;

            var referencedPath = Path.GetFullPath(Path.Combine(sourceDirectory, relativeReference));
            if (!File.Exists(referencedPath)) continue;

            var destinationPath = Path.Combine(intermediateRoot, Path.GetFileName(referencedPath));
            if (File.Exists(destinationPath)) continue;

            File.Copy(referencedPath, destinationPath, overwrite: false);
            if (verbose)
            {
                Console.WriteLine($"[unsigned-type] Copied MSBuild sibling reference: {referencedPath} -> {destinationPath}");
            }
        }
    }

    private static UnsignedTypeSourceLayout ResolveSourceLayout(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);

        if (File.Exists(fullSourcePath) && Path.GetExtension(fullSourcePath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(graph.ProjectsInTopologicalOrder.Select(p => p.ProjectDirectory));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .Append(fullSourcePath)
                .ToList();
            return new UnsignedTypeSourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        if (File.Exists(fullSourcePath) && Path.GetExtension(fullSourcePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(graph.ProjectsInTopologicalOrder.Select(p => p.ProjectDirectory));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .ToList();
            return new UnsignedTypeSourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        if (Directory.Exists(fullSourcePath))
        {
            if (ProjectDiscovery.TryResolveProjectEntry(fullSourcePath, out var entryProjectPath))
            {
                var graph = ProjectDiscovery.LoadProjectGraph(entryProjectPath);
                var sourceRoot = GetCommonRoot(graph.ProjectsInTopologicalOrder.Select(p => p.ProjectDirectory));
                var projectFiles = graph.ProjectsInTopologicalOrder
                    .Select(p => p.ProjectFilePath)
                    .ToList();

                if (Path.GetExtension(entryProjectPath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    projectFiles.Add(entryProjectPath);
                }
                else
                {
                    var slnDirs = new[] { sourceRoot, fullSourcePath };
                    foreach (var searchDir in slnDirs)
                    {
                        if (!Directory.Exists(searchDir)) continue;
                        var slnInDir = Directory.GetFiles(searchDir, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (slnInDir != null)
                        {
                            projectFiles.Add(slnInDir);
                            break;
                        }
                    }
                }

                return new UnsignedTypeSourceLayout(sourceRoot, fullSourcePath, projectFiles);
            }

            return new UnsignedTypeSourceLayout(fullSourcePath, fullSourcePath, Array.Empty<string>());
        }

        throw new FileNotFoundException("Source path not found.", fullSourcePath);
    }

    private static string MapToIntermediate(UnsignedTypeSourceLayout layout, string intermediateRoot)
    {
        if (layout.ProjectFiles.Count > 0 && Directory.Exists(layout.EntryPath))
        {
            return intermediateRoot;
        }

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

    private static string? ResolveRestoreTarget(string intermediateRoot, UnsignedTypeSourceLayout layout)
    {
        var preprocessedPath = MapToIntermediate(layout, intermediateRoot);
        if (File.Exists(preprocessedPath) &&
            (preprocessedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
             preprocessedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return preprocessedPath;
        }

        if (Directory.Exists(preprocessedPath))
        {
            var sln = Directory.GetFiles(preprocessedPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (sln != null) return sln;

            var csproj = Directory.GetFiles(preprocessedPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj != null) return csproj;
        }

        return null;
    }

    private static int RunDotNetRestore(string targetPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"restore \"{targetPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine("Failed to start dotnet restore process.");
                return -1;
            }

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

    private static ProjectGraph? TryLoadProjectGraph(UnsignedTypeSourceLayout layout, bool verbose)
    {
        if (layout.ProjectFiles.Count == 0)
        {
            return null;
        }

        try
        {
            if (ProjectDiscovery.TryResolveProjectEntry(layout.EntryPath, out var entryPath))
            {
                return ProjectDiscovery.LoadProjectGraph(entryPath);
            }
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.Error.WriteLine($"Warning: could not load the project graph for support filtering: {ex.Message}");
            }
        }

        return null;
    }

    private static void MergeStatistics(UnsignedTypeRewriterStatistics target, UnsignedTypeRewriterStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.TypesReplaced += source.TypesReplaced;
        target.OperatorsRewritten += source.OperatorsRewritten;
        target.CastsRewritten += source.CastsRewritten;
        target.ArraysRewritten += source.ArraysRewritten;
        target.GenericArgsReplaced += source.GenericArgsReplaced;
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
        var fullPath = Path.GetFullPath(path);
        var fullCandidate = Path.GetFullPath(candidateAncestor).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullPath.Equals(fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullCandidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(fullCandidate + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureExtensionFileIncluded(string intermediateRoot)
    {
        var extensionPath = Path.Combine(intermediateRoot, UnsignedExtensionsFileContent.FileName);
        if (!File.Exists(extensionPath))
        {
            return;
        }

        foreach (var projectFile in Directory.EnumerateFiles(intermediateRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(projectFile);
            if (content.Contains(UnsignedExtensionsFileContent.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var marker = "</Project>";
            var insertAt = content.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (insertAt < 0)
            {
                continue;
            }

            var projectDir = Path.GetDirectoryName(projectFile)!;
            var relative = Path.GetRelativePath(projectDir, extensionPath);
            var itemGroup = $"  <ItemGroup>\r\n    <Compile Include=\"{relative}\" />\r\n  </ItemGroup>\r\n";
            content = content.Insert(insertAt, itemGroup);
            File.WriteAllText(projectFile, content, new System.Text.UTF8Encoding(false));
        }
    }

    private sealed record UnsignedTypeSourceLayout(
        string SourceRoot,
        string EntryPath,
        IReadOnlyList<string> ProjectFiles);
}
