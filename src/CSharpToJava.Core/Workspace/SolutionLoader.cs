using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace CSharpToJava.Core.Workspace;

/// <summary>
/// Loads .sln or .csproj files using MSBuildWorkspace for full semantic resolution.
/// Provides compilations with all NuGet and project references resolved.
/// </summary>
public sealed class SolutionLoader : IDisposable
{
    private MSBuildWorkspace? _workspace;
    private bool _disposed;

    private static readonly object _locatorLock = new();
    private static bool _locatorRegistered;

    /// <summary>
    /// Registers the MSBuild locator. Must be called exactly once before any workspace operations.
    /// Thread-safe and idempotent.
    /// </summary>
    private static Dictionary<string, string>? _cachedMsbuildProperties;

    private static Dictionary<string, string> GetMSBuildProperties()
    {
        if (_cachedMsbuildProperties != null)
            return _cachedMsbuildProperties;

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var sdkPath = Environment.GetEnvironmentVariable("MSBuildSDKsPath");
        if (string.IsNullOrEmpty(sdkPath))
        {
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            var sdkBase = Path.Combine(dotnetRoot, "sdk");
            if (Directory.Exists(sdkBase))
            {
                var latestSdk = Directory.GetDirectories(sdkBase)
                    .Select(d => new { Path = d, Version = TryParseSdkVersion(Path.GetFileName(d)) })
                    .Where(x => x.Version != null)
                    .OrderByDescending(x => x.Version)
                    .FirstOrDefault();
                if (latestSdk != null)
                {
                    var sdksDir = Path.Combine(latestSdk.Path, "Sdks");
                    if (Directory.Exists(sdksDir))
                        sdkPath = sdksDir;
                }
            }
        }

        if (!string.IsNullOrEmpty(sdkPath))
            properties["MSBuildSDKsPath"] = sdkPath;

        _cachedMsbuildProperties = properties;
        return properties;
    }

    internal static Version? TryParseSdkVersion(string directoryName)
    {
        // SDK directory names look like "2.1.818" or "10.0.300".
        // Extract the leading dotted numeric version.
        var versionPart = new string(directoryName.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (Version.TryParse(versionPart, out var version))
        {
            return version;
        }
        return null;
    }

    public static bool EnsureMSBuildRegistered()
    {
        lock (_locatorLock)
        {
            if (_locatorRegistered) return true;
            try
            {
                if (!MSBuildLocator.IsRegistered)
                {
                    var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
                    var dotnetSdkInstance = instances
                        .Where(i => i.DiscoveryType == DiscoveryType.DotNetSdk)
                        .OrderByDescending(i => i.Version)
                        .FirstOrDefault();
                    if (dotnetSdkInstance != null)
                    {
                        MSBuildLocator.RegisterInstance(dotnetSdkInstance);
                    }
                    else
                    {
                        MSBuildLocator.RegisterDefaults();
                    }
                }
                _locatorRegistered = true;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Opens a solution (.sln) file and loads all C# projects.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceProject>> OpenSolutionAsync(
        string solutionPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        if (!File.Exists(solutionPath))
            throw new FileNotFoundException("Solution file not found.", solutionPath);

        _workspace = MSBuildWorkspace.Create(GetMSBuildProperties());
        _workspace.WorkspaceFailed += (_, e) =>
            progress?.Report($"Workspace warning: {e.Diagnostic.Message}");

        progress?.Report($"Opening solution: {solutionPath}");
        var solution = await _workspace.OpenSolutionAsync(solutionPath, progress: null, ct);

        return await LoadProjectsFromSolution(solution, progress, ct);
    }

    /// <summary>
    /// Opens a single project (.csproj) file.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceProject>> OpenProjectAsync(
        string projectPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (!File.Exists(projectPath))
            throw new FileNotFoundException("Project file not found.", projectPath);

        _workspace = MSBuildWorkspace.Create(GetMSBuildProperties());
        _workspace.WorkspaceFailed += (_, e) =>
            progress?.Report($"Workspace warning: {e.Diagnostic.Message}");

        progress?.Report($"Opening project: {projectPath}");
        var project = await _workspace.OpenProjectAsync(projectPath, progress: null, ct);

        return await LoadProjectsFromSolution(project.Solution, progress, ct);
    }

    /// <summary>
    /// Tries to open a path as either a solution, project, or directory containing a project.
    /// Returns null if MSBuild cannot resolve the path.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceProject>?> TryOpenAsync(
        string path,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path);
            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                return await OpenSolutionAsync(path, progress, ct);
            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                return await OpenProjectAsync(path, progress, ct);
        }

        if (Directory.Exists(path))
        {
            // Try to find a .sln first, then .csproj
            var sln = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (sln != null)
                return await OpenSolutionAsync(sln, progress, ct);

            var csprojs = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojs.Length == 1)
                return await OpenProjectAsync(csprojs[0], progress, ct);

            // Multiple .csproj — try to match directory name
            var dirName = Path.GetFileName(path);
            var match = csprojs.FirstOrDefault(p =>
                Path.GetFileNameWithoutExtension(p).Equals(dirName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return await OpenProjectAsync(match, progress, ct);
        }

        return null;
    }

    private static async Task<IReadOnlyList<WorkspaceProject>> LoadProjectsFromSolution(
        Solution solution,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var result = new List<WorkspaceProject>();

        // Topological sort: process projects in dependency order
        var projectGraph = solution.GetProjectDependencyGraph();
        var sortedIds = projectGraph.GetTopologicallySortedProjects(ct);

        foreach (var projectId in sortedIds)
        {
            ct.ThrowIfCancellationRequested();

            var project = solution.GetProject(projectId);
            if (project == null || project.Language != LanguageNames.CSharp)
                continue;

            progress?.Report($"Compiling: {project.Name}");
            var compilation = await project.GetCompilationAsync(ct) as CSharpCompilation;
            if (compilation == null || !compilation.SyntaxTrees.Any())
            {
                progress?.Report($"Skipping {project.Name}: compilation is empty (MSBuild may have failed to evaluate the project).");
                continue;
            }

            var documents = project.Documents
                .Where(d => d.SourceCodeKind == SourceCodeKind.Regular
                            && d.FilePath != null
                            && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var projectRefs = project.ProjectReferences
                .Select(pr => solution.GetProject(pr.ProjectId)?.FilePath)
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();

            var isTest = IsTestProject(project);
            var resources = ScanProjectResources(project.FilePath);

            result.Add(new WorkspaceProject
            {
                Name = project.Name,
                FilePath = project.FilePath ?? "",
                Directory = Path.GetDirectoryName(project.FilePath) ?? "",
                Compilation = compilation,
                Documents = documents,
                ProjectReferences = projectRefs,
                IsTestProject = isTest,
                ResourceItems = resources,
            });
        }

        return result;
    }

    private static bool IsTestProject(Project project)
    {
        // Check by common test package references
        var testPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.NET.Test.Sdk",
            "xunit", "xunit.core", "xunit.runner.visualstudio",
            "NUnit", "NUnit3TestAdapter",
            "MSTest.TestFramework", "MSTest.TestAdapter"
        };

        foreach (var reference in project.MetadataReferences)
        {
            if (reference is PortableExecutableReference peRef && peRef.FilePath != null)
            {
                var fileName = Path.GetFileNameWithoutExtension(peRef.FilePath);
                if (testPackages.Contains(fileName))
                    return true;
            }
        }

        // Heuristic: project name contains "Test" or "Tests"
        return project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scans a .csproj file for &lt;None&gt; and &lt;Content&gt; items with
    /// &lt;CopyToOutputDirectory&gt; set to a value other than "Never", and for
    /// &lt;EmbeddedResource&gt; items (which are always copied using their logical
    /// name when provided).
    /// </summary>
    internal static IReadOnlyList<ResourceItem> ScanProjectResources(string? projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
            return [];

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? string.Empty;
        var resources = new List<ResourceItem>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        XDocument doc;
        try
        {
            doc = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return [];
        }

        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName is "None" or "Content" or "EmbeddedResource"))
        {
            var include = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update");
            if (string.IsNullOrWhiteSpace(include))
                continue;

            var isEmbeddedResource = item.Name.LocalName == "EmbeddedResource";
            if (!isEmbeddedResource)
            {
                var copyBehavior = item.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "CopyToOutputDirectory")?.Value;
                if (string.IsNullOrWhiteSpace(copyBehavior)
                    || copyBehavior.Equals("Never", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var normalizedInclude = include.Replace('\\', Path.DirectorySeparatorChar)
                                           .Replace('/', Path.DirectorySeparatorChar);
            var logicalNameTemplate = item.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "LogicalName")?.Value;

            if (normalizedInclude.Contains('*'))
            {
                // Expand wildcard patterns
                var patternDir = Path.GetDirectoryName(normalizedInclude) ?? "";
                var patternFile = Path.GetFileName(normalizedInclude);
                var searchDir = Path.GetFullPath(Path.Combine(projectDir, patternDir));

                if (!Directory.Exists(searchDir))
                    continue;

                foreach (var matchedFile in Directory.GetFiles(searchDir, patternFile))
                {
                    var fileName = Path.GetFileName(matchedFile);
                    var relativePath = string.IsNullOrEmpty(patternDir)
                        ? fileName
                        : Path.Combine(patternDir, fileName)
                              .Replace('\\', Path.DirectorySeparatorChar)
                              .Replace('/', Path.DirectorySeparatorChar);

                    if (!addedPaths.Add(matchedFile))
                        continue;

                    resources.Add(new ResourceItem
                    {
                        SourcePath = matchedFile,
                        RelativePath = ExpandEmbeddedResourceLogicalName(logicalNameTemplate, matchedFile, projectDir, normalizedInclude) ?? relativePath,
                    });
                }
            }
            else
            {
                var fullPath = Path.GetFullPath(Path.Combine(projectDir, normalizedInclude));
                if (!File.Exists(fullPath) || !addedPaths.Add(fullPath))
                    continue;

                resources.Add(new ResourceItem
                {
                    SourcePath = fullPath,
                    RelativePath = ExpandEmbeddedResourceLogicalName(logicalNameTemplate, fullPath, projectDir, normalizedInclude) ?? normalizedInclude,
                });
            }
        }

        return resources;
    }

    /// <summary>
    /// Expands the most common MSBuild item metadata tokens used in an
    /// &lt;EmbeddedResource LogicalName=&quot;...&quot;/&gt; value. Returns null when no
    /// logical name template was supplied.
    /// </summary>
    private static string? ExpandEmbeddedResourceLogicalName(string? logicalNameTemplate, string filePath, string projectDir, string identity)
    {
        if (string.IsNullOrWhiteSpace(logicalNameTemplate))
            return null;

        var fullPath = Path.GetFullPath(filePath);
        var fileName = Path.GetFileName(fullPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var rootDir = Path.GetPathRoot(fullPath) ?? string.Empty;
        var relativePath = fullPath;
        try
        {
            relativePath = Path.GetRelativePath(projectDir, fullPath);
        }
        catch
        {
            // Fall back to the full path if relative-path computation fails.
        }
        var relativeDir = Path.GetDirectoryName(relativePath);
        relativeDir = string.IsNullOrEmpty(relativeDir) ? string.Empty : relativeDir + Path.DirectorySeparatorChar;

        var recursiveDir = string.Empty;
        var identityDir = Path.GetDirectoryName(identity) ?? string.Empty;
        if (!string.IsNullOrEmpty(identityDir) && !identityDir.Contains('*'))
        {
            var relativeToIdentity = relativePath;
            if (relativeToIdentity.StartsWith(identityDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                recursiveDir = Path.GetDirectoryName(relativeToIdentity[(identityDir.Length + 1)..]) ?? string.Empty;
                if (!string.IsNullOrEmpty(recursiveDir))
                    recursiveDir += Path.DirectorySeparatorChar;
            }
        }

        var result = logicalNameTemplate;
        result = result.Replace("%(Filename)", fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("%(Extension)", extension, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("%(RelativeDir)", relativeDir, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("%(RecursiveDir)", recursiveDir, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("%(Identity)", identity, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("%(FullPath)", fullPath, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("%(Directory)", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("%(RootDir)", rootDir, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("%(FullPath)", fullPath, StringComparison.OrdinalIgnoreCase);

        return result.Replace('\\', '/');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace?.Dispose();
    }
}
