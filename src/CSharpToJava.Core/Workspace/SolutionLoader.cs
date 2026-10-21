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

    private static readonly object FallbackReferencesLock = new();
    private static IReadOnlyList<MetadataReference>? _fallbackReferences;

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
    /// Opens an XML solution (.slnx) file. Roslyn's MSBuildWorkspace does not
    /// evaluate .slnx natively, so the project entries are parsed here and loaded
    /// one by one into the same workspace.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceProject>> OpenSlnxAsync(
        string solutionPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        if (!File.Exists(solutionPath))
            throw new FileNotFoundException("Solution file not found.", solutionPath);

        var projectPaths = ReadSlnxProjectPaths(solutionPath);
        if (projectPaths.Count == 0)
            throw new InvalidOperationException($"No project entries found in solution '{solutionPath}'.");

        _workspace = MSBuildWorkspace.Create(GetMSBuildProperties());
        _workspace.WorkspaceFailed += (_, e) =>
            progress?.Report($"Workspace warning: {e.Diagnostic.Message}");

        progress?.Report($"Opening solution: {solutionPath}");
        foreach (var projectPath in projectPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report($"Opening project: {projectPath}");
                await _workspace.OpenProjectAsync(projectPath, progress: null, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One broken project must not abort the remaining modules.
                progress?.Report($"Failed to open project '{projectPath}': {ex.Message}");
            }
        }

        return await LoadProjectsFromSolution(_workspace.CurrentSolution, progress, ct);
    }

    /// <summary>
    /// Reads the <c>&lt;Project Path="..."/&gt;</c> entries of an .slnx solution
    /// (they may be nested inside <c>&lt;Folder&gt;</c> nodes). Paths are resolved
    /// relative to the solution directory; missing files, unresolvable MSBuild
    /// expressions and duplicates are skipped.
    /// </summary>
    internal static IReadOnlyList<string> ReadSlnxProjectPaths(string solutionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? string.Empty;

        XDocument document;
        try
        {
            document = XDocument.Load(solutionPath);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return [];
        }

        if (document.Root == null)
            return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectElement in document.Root.Descendants("Project"))
        {
            var pathAttribute = projectElement.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(pathAttribute))
                continue;
            // Skip MSBuild-property / wildcard entries — they cannot be resolved
            // without a full MSBuild evaluation.
            if (pathAttribute.Contains('$') || pathAttribute.Contains('*') || pathAttribute.Contains('?'))
                continue;

            string fullPath;
            try
            {
                var relativePath = pathAttribute
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                fullPath = Path.GetFullPath(Path.Combine(solutionDirectory, relativePath));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!File.Exists(fullPath) || !seen.Add(fullPath))
                continue;

            result.Add(fullPath);
        }

        return result;
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
            if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                return await OpenSlnxAsync(path, progress, ct);
            if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                return await OpenProjectAsync(path, progress, ct);
        }

        if (Directory.Exists(path))
        {
            // Try to find a .sln first, then .slnx, then .csproj
            var sln = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (sln != null)
                return await OpenSolutionAsync(sln, progress, ct);

            var slnx = Directory.GetFiles(path, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (slnx != null)
                return await OpenSlnxAsync(slnx, progress, ct);

            var csprojs = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojs.Length == 1)
                return await OpenProjectAsync(csprojs[0], progress, ct);

            // Multiple .csproj — try to match directory name
            var dirName = Path.GetFileName(path);
            var match = csprojs.FirstOrDefault(p =>
                Path.GetFileNameWithoutExtension(p).Equals(dirName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return await OpenProjectAsync(match, progress, ct);

            // Preprocessed source trees keep the entry solution at its original
            // repository-relative location (e.g. <stage>\json\System.Text.Json.sln)
            // rather than at the stage root. Resolving it keeps the conversion on the
            // MSBuild multi-module pipeline instead of the flat directory scan.
            if (TryResolveNestedSolutionEntry(path, out var nestedSolution))
            {
                return Path.GetExtension(nestedSolution).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                    ? await OpenSlnxAsync(nestedSolution, progress, ct)
                    : await OpenSolutionAsync(nestedSolution, progress, ct);
            }

            // Projects nested in immediate subdirectories (e.g. <root>\src\Lib.csproj
            // plus <root>\tests\App.csproj referencing it). Open the referencing root
            // project so MSBuildWorkspace loads the full dependency closure and the
            // conversion produces one Maven module per C# project instead of falling
            // back to the flat single-module directory scan.
            if (TryResolveNestedProjectEntry(path, out var nestedEntry))
                return await OpenProjectAsync(nestedEntry, progress, ct);
        }

        return null;
    }

    /// <summary>
    /// Resolves the solution (.sln or .slnx) of a directory source whose solution lives one level
    /// below the directory itself — the layout produced by the preprocessing stages,
    /// which preserve the entry solution at its original repository-relative path
    /// (e.g. <c>&lt;stage&gt;\json\System.Text.Json.sln</c>). A top-level solution always
    /// wins; a single nested solution is unambiguous, while several unrelated
    /// solutions decline so the caller can fall back.
    /// </summary>
    public static bool TryResolveNestedSolutionEntry(string directory, out string solutionPath)
    {
        solutionPath = string.Empty;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return false;

        var topLevel = Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(directory, "*.slnx", SearchOption.TopDirectoryOnly))
            .ToArray();
        if (topLevel.Length > 1)
            return false;
        if (topLevel.Length == 1)
        {
            solutionPath = Path.GetFullPath(topLevel[0]);
            return true;
        }

        var nested = new List<string>();
        foreach (var subDirectory in Directory.EnumerateDirectories(directory))
        {
            try
            {
                nested.AddRange(Directory.EnumerateFiles(subDirectory, "*.sln", SearchOption.TopDirectoryOnly));
                nested.AddRange(Directory.EnumerateFiles(subDirectory, "*.slnx", SearchOption.TopDirectoryOnly));
            }
            catch (IOException)
            {
                // Unreadable subdirectory — skip it.
            }
        }

        if (nested.Count != 1)
            return false;

        solutionPath = Path.GetFullPath(nested[0]);
        return true;
    }

    /// <summary>
    /// Resolves the entry project for a directory whose .csproj files live in
    /// immediate subdirectories (the common <c>src/</c> + <c>tests/</c> layout).
    /// The entry is the candidate that is NOT referenced by any other candidate
    /// (the root of the ProjectReference graph); opening it loads the whole
    /// closure. Ambiguous layouts (no projects, several unrelated roots, or a
    /// reference cycle) return false so the caller can fall back.
    /// </summary>
    internal static bool TryResolveNestedProjectEntry(string directory, out string entryProjectPath)
    {
        entryProjectPath = string.Empty;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return false;

        var candidates = new List<string>();
        foreach (var subDirectory in Directory.EnumerateDirectories(directory))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(subDirectory, "*.csproj", SearchOption.TopDirectoryOnly));
            }
            catch (IOException)
            {
                // Unreadable subdirectory — skip it.
            }
        }
        if (candidates.Count == 0)
            return false;

        var candidateSet = new HashSet<string>(
            candidates.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidateSet)
        {
            foreach (var referencePath in EnumerateProjectReferences(candidate))
            {
                referenced.Add(referencePath);
            }
        }

        var roots = candidateSet.Where(c => !referenced.Contains(c)).ToList();
        if (roots.Count != 1)
            return false;

        entryProjectPath = roots[0];
        return true;
    }

    /// <summary>
    /// Enumerates the fully-qualified target paths of a project's
    /// <c>&lt;ProjectReference Include="..." /&gt;</c> items (plain relative paths only;
    /// wildcard or MSBuild-property includes are ignored, matching the lightweight
    /// discovery used by ProjectDiscovery).
    /// </summary>
    private static IEnumerable<string> EnumerateProjectReferences(string projectPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectPath);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            yield break;
        }

        if (document.Root == null)
            yield break;

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (string.IsNullOrEmpty(baseDirectory))
            yield break;

        foreach (var projectReference in document.Root.Descendants("ProjectReference"))
        {
            var include = projectReference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                continue;
            // Skip MSBuild-property / wildcard includes — they cannot be resolved
            // without a full MSBuild evaluation.
            if (include.Contains('$') || include.Contains('*') || include.Contains('?'))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(baseDirectory, include));
            }
            catch (ArgumentException)
            {
                continue;
            }
            yield return fullPath;
        }
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
            // MSBuildWorkspace ????????/????????????? netstandard2.0
            // ???? NETStandard.Library ???????????????????????
            // ??? pass?LINQ ????????????????????????????
            // ?????? Java ???????? dotnet packs ?????????????
            // ?? netstandard2.0 ???????? ref ?? API ???????
            if (compilation != null)
            {
                compilation = EnsureFrameworkReferences(compilation);
            }
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

    /// <summary>
    /// ???? BCL ???netstandard.dll/mscorlib.dll/System.Runtime.dll???????
    /// ?????? dotnet packs ?? ref ????Microsoft.NETCore.App.Ref ???
    /// NETStandard.Library.Ref ?????????????
    /// </summary>
    internal static CSharpCompilation EnsureFrameworkReferences(CSharpCompilation compilation)
    {
        if (HasFrameworkReference(compilation))
        {
            return compilation;
        }

        var fallback = GetFallbackFrameworkReferences();
        return fallback.Count == 0 ? compilation : compilation.AddReferences(fallback);
    }

    private static bool HasFrameworkReference(CSharpCompilation compilation)
    {
        foreach (var reference in compilation.References)
        {
            if (reference is PortableExecutableReference pe && pe.FilePath != null)
            {
                var fileName = Path.GetFileName(pe.FilePath);
                if (fileName is "netstandard.dll" or "mscorlib.dll" or "System.Runtime.dll")
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// ??? dotnet packs ???????????????????
    /// </summary>
    internal static IReadOnlyList<MetadataReference> GetFallbackFrameworkReferences()
    {
        lock (FallbackReferencesLock)
        {
            if (_fallbackReferences != null)
            {
                return _fallbackReferences;
            }

            var paths = new List<string>();
            foreach (var programFiles in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                if (string.IsNullOrEmpty(programFiles)) continue;
                var packsRoot = Path.Combine(programFiles, "dotnet", "packs");
                paths = CollectRefAssembliesFromPacks(packsRoot);
                if (paths.Count > 0) break;
            }

            _fallbackReferences = paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f))
                .ToList();
            return _fallbackReferences;
        }
    }

    private static List<string> CollectRefAssembliesFromPacks(string packsRoot)
    {
        var result = new List<string>();

        // ? Microsoft.NETCore.App.Ref/<??>/ref/<tfm>/*.dll?? System.Text.Json ??
        //    ????????????????
        var coreAppRef = Path.Combine(packsRoot, "Microsoft.NETCore.App.Ref");
        var bestVersionDir = PickNewestVersionDirectory(coreAppRef);
        if (bestVersionDir != null)
        {
            var refRoot = Path.Combine(bestVersionDir, "ref");
            if (Directory.Exists(refRoot))
            {
                var tfmDir = Directory.GetDirectories(refRoot)
                    .OrderByDescending(GetTfmNumericSuffix)
                    .FirstOrDefault();
                if (tfmDir != null)
                {
                    result.AddRange(Directory.GetFiles(tfmDir, "*.dll"));
                    return result;
                }
            }
        }

        // ? ???NETStandard.Library.Ref/<??>/ref/netstandard2.1/*.dll?
        var netStandardRef = Path.Combine(packsRoot, "NETStandard.Library.Ref");
        var netStandardVersionDir = PickNewestVersionDirectory(netStandardRef);
        if (netStandardVersionDir != null)
        {
            var refRoot = Path.Combine(netStandardVersionDir, "ref");
            if (Directory.Exists(refRoot))
            {
                foreach (var tfmDir in Directory.GetDirectories(refRoot)
                             .OrderByDescending(GetTfmNumericSuffix))
                {
                    result.AddRange(Directory.GetFiles(tfmDir, "*.dll"));
                    if (result.Count > 0) break;
                }
            }
        }

        return result;
    }

    private static string? PickNewestVersionDirectory(string packRoot)
    {
        if (!Directory.Exists(packRoot))
        {
            return null;
        }
        return Directory.GetDirectories(packRoot)
            .Select(d => new { Dir = d, Version = TryParseSdkVersion(Path.GetFileName(d)) })
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    /// <summary>
    /// TFM ????net10.0/net8.0/netstandard2.1?? ??????????? TFM?
    /// ??????? -1?
    /// </summary>
    private static double GetTfmNumericSuffix(string directoryPath)
    {
        var name = Path.GetFileName(directoryPath);
        var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return double.TryParse(digits, out var value) ? value : -1;
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
                // Expand wildcard patterns (single * and recursive ** globs).
                foreach (var matchedFile in ProjectGlobExpander.Expand(projectDir, normalizedInclude))
                {
                    var relativePath = Path.GetRelativePath(projectDir, matchedFile)
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

        // SDK-style projects default EmbeddedResource items to **/*.resx under the
        // project (Resources/ is the convention). They are NOT listed in the csproj,
        // so scan them explicitly — otherwise SR.getString (ResourceManager) resolves
        // nothing and falls back to "{Name}" (e.g. JSON test data becomes invalid).
        try
        {
            var resxFiles = Directory.EnumerateFiles(projectDir, "*.resx", SearchOption.AllDirectories);
            foreach (var resxFile in resxFiles)
            {
                if (!addedPaths.Add(resxFile))
                    continue;
                var relativePath = Path.GetRelativePath(projectDir, resxFile)
                                  .Replace('\\', Path.DirectorySeparatorChar)
                                  .Replace('/', Path.DirectorySeparatorChar);
                resources.Add(new ResourceItem
                {
                    SourcePath = resxFile,
                    RelativePath = relativePath,
                });
            }
        }
        catch
        {
            // Directory enumeration failure is not fatal; explicit items still work.
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
