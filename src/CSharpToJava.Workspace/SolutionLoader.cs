using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace CSharpToJava.Workspace;

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
    public static bool EnsureMSBuildRegistered()
    {
        lock (_locatorLock)
        {
            if (_locatorRegistered) return true;
            try
            {
                if (!MSBuildLocator.IsRegistered)
                {
                    MSBuildLocator.RegisterDefaults();
                }
                _locatorRegistered = true;
                return true;
            }
            catch (InvalidOperationException)
            {
                // MSBuild SDK not found
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

        _workspace = MSBuildWorkspace.Create();
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

        _workspace = MSBuildWorkspace.Create();
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
            if (compilation == null)
                continue;

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

            result.Add(new WorkspaceProject
            {
                Name = project.Name,
                FilePath = project.FilePath ?? "",
                Directory = Path.GetDirectoryName(project.FilePath) ?? "",
                Compilation = compilation,
                Documents = documents,
                ProjectReferences = projectRefs,
                IsTestProject = isTest
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace?.Dispose();
    }
}
