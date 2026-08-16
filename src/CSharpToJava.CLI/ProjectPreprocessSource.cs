namespace CSharpToJava.CLI;

internal sealed record ProjectPreprocessSourceLayout(
    string SourceRoot,
    string EntryPath,
    IReadOnlyList<string> ProjectFiles);

/// <summary>
/// Shared source-tree preparation used by the unsigned-type, readonly-struct and
/// goto preprocessors. Keeping one implementation guarantees every intermediate
/// directory (".cs2j-unsigned-src", ".cs2j-readonly-src", ".cs2j-no-goto-src")
/// selects and copies exactly the same supported projects.
/// </summary>
internal static class ProjectPreprocessSource
{
    internal static readonly string[] MsBuildDirectoryFileNames =
    {
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "nuget.config",
    };

    internal static ProjectPreprocessSourceLayout ResolveLayout(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);

        if (File.Exists(fullSourcePath) && Path.GetExtension(fullSourcePath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(
                graph.ProjectsInTopologicalOrder
                    .Select(p => p.ProjectDirectory)
                    .Append(Path.GetDirectoryName(fullSourcePath)!));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .Append(fullSourcePath)
                .ToList();
            return new ProjectPreprocessSourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        if (File.Exists(fullSourcePath) && Path.GetExtension(fullSourcePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(
                graph.ProjectsInTopologicalOrder
                    .Select(p => p.ProjectDirectory)
                    .Append(Path.GetDirectoryName(fullSourcePath)!));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .ToList();
            return new ProjectPreprocessSourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        if (Directory.Exists(fullSourcePath))
        {
            if (ProjectDiscovery.TryResolveProjectEntry(fullSourcePath, out var entryProjectPath))
            {
                var graph = ProjectDiscovery.LoadProjectGraph(entryProjectPath);
                var sourceRoot = GetCommonRoot(
                    graph.ProjectsInTopologicalOrder
                        .Select(p => p.ProjectDirectory)
                        .Append(fullSourcePath));
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

                return new ProjectPreprocessSourceLayout(sourceRoot, fullSourcePath, projectFiles);
            }

            return new ProjectPreprocessSourceLayout(fullSourcePath, fullSourcePath, Array.Empty<string>());
        }

        throw new FileNotFoundException("Source path not found.", fullSourcePath);
    }

    internal static IEnumerable<string> EnumerateSourceFiles(
        string sourceRoot,
        string destinationRoot,
        string? outputRoot = null)
    {
        var fullOutputRoot = outputRoot == null ? null : Path.GetFullPath(outputRoot);
        var fullSourceRoot = Path.GetFullPath(sourceRoot);
        var fullDestinationRoot = Path.GetFullPath(destinationRoot);
        var skipOutputRoot = fullOutputRoot != null
                             && IsSameOrDescendantPath(fullOutputRoot, fullSourceRoot)
                             && !string.Equals(fullOutputRoot, fullSourceRoot, StringComparison.OrdinalIgnoreCase);

        var pending = new Stack<string>();
        pending.Push(fullSourceRoot);

        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            if (IsSameOrDescendantPath(currentDirectory, fullDestinationRoot)
                || (skipOutputRoot && fullOutputRoot != null && IsSameOrDescendantPath(currentDirectory, fullOutputRoot)))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (SupportedProjectFiltering.IgnoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                {
                    continue;
                }

                if (IsSameOrDescendantPath(subDirectory, fullDestinationRoot)
                    || (skipOutputRoot && fullOutputRoot != null && IsSameOrDescendantPath(subDirectory, fullOutputRoot)))
                {
                    continue;
                }

                pending.Push(Path.GetFullPath(subDirectory));
            }
        }
    }

    internal static async Task CopyProjectFilesAsync(
        ProjectPreprocessSourceLayout layout,
        string sourceRoot,
        string intermediateRoot,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        foreach (var projectFile in layout.ProjectFiles)
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var relativePath = projectFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(projectFile)
                : Path.GetRelativePath(sourceRoot, projectFile);
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
                try
                {
                    File.Copy(projectFile, destinationFile, overwrite: true);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"CopyProjectFilesAsync failed. EntryPath={layout.EntryPath}, SourceRoot={sourceRoot}, IntermediateRoot={intermediateRoot}, " +
                        $"Source={projectFile}, Destination={destinationFile}. {ex.Message}",
                        ex);
                }
            }
        }
    }

    internal static void CopyAncestorMsBuildDirectoryFiles(
        string sourceRoot,
        string intermediateRoot,
        string logPrefix,
        bool verbose)
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
                Console.WriteLine($"{logPrefix} Copied MSBuild directory file: {found} -> {destination}");
            }

            CopyMsBuildThisFileDirectoryReferences(found, intermediateRoot, logPrefix, verbose);
        }

        EnsurePreserializedResourceSupport(intermediateRoot, logPrefix, verbose);
    }

    internal static string MapToIntermediate(ProjectPreprocessSourceLayout layout, string intermediateRoot)
    {
        if (layout.ProjectFiles.Count > 0 && Directory.Exists(layout.EntryPath))
        {
            return intermediateRoot;
        }

        if (File.Exists(layout.EntryPath))
        {
            if (layout.EntryPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(intermediateRoot, Path.GetFileName(layout.EntryPath));
            }

            return Path.Combine(intermediateRoot, Path.GetRelativePath(layout.SourceRoot, layout.EntryPath));
        }

        if (string.Equals(layout.SourceRoot, layout.EntryPath, StringComparison.OrdinalIgnoreCase))
        {
            return intermediateRoot;
        }

        return Path.Combine(intermediateRoot, Path.GetRelativePath(layout.SourceRoot, layout.EntryPath));
    }

    internal static string? ResolveRestoreTarget(
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot)
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

    internal static int RunDotNetRestore(string targetPath, string logPrefix)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"restore \"{targetPath}\" --ignore-failed-sources",
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
                Console.Error.WriteLine($"{logPrefix} Failed to start dotnet restore process.");
                return -1;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            System.Threading.Tasks.Task.WaitAll(outputTask, errorTask);

            if (process.ExitCode != 0)
            {
                var output = outputTask.Result;
                var error = errorTask.Result;
                Console.Error.WriteLine($"{logPrefix} dotnet restore exited with code {process.ExitCode} for {targetPath}");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Console.Error.WriteLine($"stdout: {output}");
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    Console.Error.WriteLine($"stderr: {error}");
                }
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{logPrefix} dotnet restore failed: {ex.Message}");
            return -1;
        }
    }

    internal static ProjectGraph? TryLoadProjectGraph(ProjectPreprocessSourceLayout layout, bool verbose)
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

    internal static string GetCommonRoot(IEnumerable<string> paths)
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

    internal static bool IsSameOrDescendantPath(string path, string candidateAncestor)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidateAncestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullPath.Equals(fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullCandidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(fullCandidate + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyMsBuildThisFileDirectoryReferences(
        string sourceFile,
        string intermediateRoot,
        string logPrefix,
        bool verbose)
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
                Console.WriteLine($"{logPrefix} Copied MSBuild sibling reference: {referencedPath} -> {destinationPath}");
            }
        }
    }

    /// <summary>
    /// .NET SDK 10 fails net4x builds that contain non-string resources with
    /// MSB3823/MSB3822 unless <c>GenerateResourceUsePreserializedResources</c> is
    /// enabled and the <c>System.Resources.Extensions</c> assembly is available to
    /// the GenerateResource task. Every preprocessor applies the same targets file
    /// so the intermediate trees stay structurally identical.
    /// </summary>
    private static void EnsurePreserializedResourceSupport(
        string intermediateRoot,
        string logPrefix,
        bool verbose)
    {
        var assemblyPath = ResolveSystemResourcesExtensionsAssembly();
        if (assemblyPath == null)
        {
            if (verbose)
            {
                Console.WriteLine(
                    $"{logPrefix} System.Resources.Extensions not found in NuGet cache; " +
                    "preserialized-resource fallback will not be injected.");
            }

            return;
        }

        var targetsPath = Path.Combine(intermediateRoot, "Directory.Build.targets");
        var escapedPath = System.Security.SecurityElement.Escape(assemblyPath);
        var snippet =
            "  <ItemGroup Condition=\"'$(GenerateResourceUsePreserializedResources)' == 'true'" +
            " and '$(TargetFrameworkIdentifier)' == '.NETFramework'\">\n" +
            $"    <ReferencePathWithRefAssemblies Include=\"{escapedPath}\" />\n" +
            "  </ItemGroup>\n";

        if (File.Exists(targetsPath))
        {
            var content = File.ReadAllText(targetsPath);
            var closeTag = "</Project>";
            var closeIndex = content.LastIndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
            if (closeIndex >= 0)
            {
                content = content.Insert(closeIndex, "\n" + snippet);
            }
            else
            {
                content += "\n" + snippet;
            }

            File.WriteAllText(targetsPath, content);
        }
        else
        {
            var fullProject =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<Project>\n" + snippet + "</Project>\n";
            File.WriteAllText(targetsPath, fullProject);
        }

        if (verbose)
        {
            Console.WriteLine(
                $"{logPrefix} Injected System.Resources.Extensions reference ({assemblyPath}) " +
                "for preserialized resource verification builds.");
        }
    }

    private static string? ResolveSystemResourcesExtensionsAssembly()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile))
        {
            return null;
        }

        var packageRoot = Path.Combine(userProfile, ".nuget", "packages", "system.resources.extensions");
        if (!Directory.Exists(packageRoot))
        {
            return null;
        }

        var bestVersion = Directory.EnumerateDirectories(packageRoot)
            .Select(Path.GetFileName)
            .Where(v => !string.IsNullOrEmpty(v) && Version.TryParse(v, out _))
            .OrderByDescending(v => Version.Parse(v!))
            .FirstOrDefault();
        if (bestVersion == null)
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(packageRoot, bestVersion, "lib", "netstandard2.0", "System.Resources.Extensions.dll"),
            Path.Combine(packageRoot, bestVersion, "lib", "net462", "System.Resources.Extensions.dll"),
            Path.Combine(packageRoot, bestVersion, "lib", "net461", "System.Resources.Extensions.dll"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
