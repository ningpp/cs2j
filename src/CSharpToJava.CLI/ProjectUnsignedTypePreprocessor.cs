using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Xml.Linq;

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

        var sourceFiles = EnumerateSourceFiles(
                sourceRoot, intermediateRoot, Path.GetFullPath(request.DestinationRoot))
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

        var rewrittenSourceFiles = new List<string>();
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

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                rewrittenSourceFiles.Add(sourceFile);
                await File.WriteAllTextAsync(destinationFile, result.OutputCode, new System.Text.UTF8Encoding(false));
            }

            if (request.Verbose)
            {
                Console.WriteLine($"[unsigned-type] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        if (statistics.OperatorsRewritten > 0 || statistics.CastsRewritten > 0)
        {
            await WriteExtensionFilesAsync(
                intermediateRoot, sourceRoot, layout, exclusionInfo, rewrittenSourceFiles, projectGraph);
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
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}. " +
                    "Package references may not be resolved, leading to incorrect code generation."));
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

    private static IEnumerable<string> EnumerateSourceFiles(
        string sourceRoot, string intermediateRoot, string outputRoot)
    {
        var fullOutputRoot = Path.GetFullPath(outputRoot);
        var fullSourceRoot = Path.GetFullPath(sourceRoot);
        // 当输出目录位于源树内部（如测试 fixture 的 .test-output-sln）时，
        // 跳过整个输出目录，避免把上次生成的中间产物当作输入再次处理。
        var skipOutputRoot = IsSameOrDescendantPath(fullOutputRoot, fullSourceRoot)
                             && !string.Equals(fullOutputRoot, fullSourceRoot, StringComparison.OrdinalIgnoreCase);

        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            if (IsSameOrDescendantPath(currentDirectory, intermediateRoot)
                || (skipOutputRoot && IsSameOrDescendantPath(currentDirectory, fullOutputRoot)))
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

                if (IsSameOrDescendantPath(subDirectory, intermediateRoot)
                    || (skipOutputRoot && IsSameOrDescendantPath(subDirectory, fullOutputRoot)))
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
        var fullPath = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidateAncestor).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullPath.Equals(fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullCandidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(fullCandidate + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 数学辅助文件只写一份到中间根目录；每个实际编译被重写源文件的项目通过
    /// Compile Include + Link 引用同一物理文件，不复制本地副本。扩展类为
    /// internal，每个程序集自包含一份。
    /// </summary>
    private static async Task WriteExtensionFilesAsync(
        string intermediateRoot,
        string sourceRoot,
        UnsignedTypeSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph)
    {
        var content = UnsignedMathFileContent.Generate();
        var rootPath = Path.Combine(intermediateRoot, UnsignedMathFileContent.FileName);
        Directory.CreateDirectory(intermediateRoot);
        await File.WriteAllTextAsync(rootPath, content, new System.Text.UTF8Encoding(false));

        var hostProjectDirectories = ComputeAllExtensionHostProjectDirectories(
            layout, exclusionInfo, rewrittenFiles, projectGraph);
        EnsureExtensionCompileItems(sourceRoot, intermediateRoot, layout, exclusionInfo, hostProjectDirectories);
    }

    /// <summary>
    /// 计算需要承载扩展方法文件的项目目录：项目实际编译了至少一个被重写的源文件
    /// （目录内默认 glob，或 csproj 显式 Compile Include 解析到被重写文件）。
    /// </summary>
    private static IReadOnlySet<string> ComputeAllExtensionHostProjectDirectories(
        UnsignedTypeSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph)
    {
        if (projectGraph == null || rewrittenFiles.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return ComputeProjectsCompilingRewrittenFiles(layout, exclusionInfo, rewrittenFiles);
    }

    /// <summary>
    /// 收集实际会编译至少一个被重写源文件的项目目录：目录内直接包含被重写文件
    /// （SDK 默认 glob 场景），或 csproj 的 Compile Include 解析到被重写文件。
    /// </summary>
    private static IReadOnlySet<string> ComputeProjectsCompilingRewrittenFiles(
        UnsignedTypeSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rewrittenFiles.Count == 0)
        {
            return result;
        }

        var rewrittenFileSet = new HashSet<string>(rewrittenFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (rewrittenFiles.Any(file => IsSameOrDescendantPath(file, projectDirectory)))
            {
                result.Add(projectDirectory);
            }
        }

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (result.Contains(projectDirectory))
            {
                continue;
            }

            XDocument projectDocument;
            try
            {
                projectDocument = XDocument.Load(projectFile, LoadOptions.None);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var compileElement in projectDocument.Descendants().Where(e => e.Name.LocalName == "Compile"))
            {
                var include = (string?)compileElement.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include!));
                if (rewrittenFileSet.Contains(resolvedPath))
                {
                    result.Add(projectDirectory);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 对每个编译被重写源文件的项目，把共享辅助文件追加到 csproj 的
    /// Compile Include（带 Link），并先清理旧的同名条目。
    /// </summary>
    private static void EnsureExtensionCompileItems(
        string sourceRoot,
        string intermediateRoot,
        UnsignedTypeSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories)
    {
        var sharedFilePath = Path.Combine(intermediateRoot, UnsignedMathFileContent.FileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDirectory))
            {
                continue;
            }

            var destinationProjectFile = Path.Combine(
                intermediateRoot,
                Path.GetRelativePath(sourceRoot, projectFile));
            EnsureExtensionCompileItem(
                destinationProjectFile,
                UnsignedMathFileContent.FileName,
                Path.GetDirectoryName(destinationProjectFile)!,
                sharedFilePath);
        }
    }

    private static void EnsureExtensionCompileItem(
        string projectFilePath,
        string fileName,
        string projectDirectory,
        string sharedFilePath)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFullPath(projectDirectory).TrimEnd('\\', '/'),
                Path.GetFullPath(Path.GetDirectoryName(sharedFilePath)!).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            // 项目根目录就是辅助文件所在根目录且使用 SDK 默认 glob：
            // 文件已被默认编译项包含，追加显式条目会造成重复编译。
            return;
        }

        var normalizedFileName = fileName.Replace('\\', '/');
        foreach (var compileElement in root.Descendants().Where(e => e.Name.LocalName == "Compile").ToList())
        {
            var include = (string?)compileElement.Attribute("Include");
            var link = (string?)compileElement.Attribute("Link");
            if (MatchesHelperFile(include, normalizedFileName)
                || MatchesHelperFile(link, normalizedFileName))
            {
                compileElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, sharedFilePath);
        var compileItem = new XElement(
            "Compile",
            new XAttribute("Include", relativeInclude),
            new XAttribute("Link", fileName));
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup", compileItem),
            new XText(Environment.NewLine));

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool MatchesHelperFile(string? value, string normalizedFileName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value!.Replace('\\', '/');
        return normalized.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("/" + normalizedFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetPropertyValue(XElement root, string propertyName)
    {
        return root.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => (string?)element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record UnsignedTypeSourceLayout(
        string SourceRoot,
        string EntryPath,
        IReadOnlyList<string> ProjectFiles);
}
