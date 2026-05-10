using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CSharpToJava.CLI;

internal enum ProjectKind
{
    Production,
    Test,
    Tool,
    Unknown,
}

internal sealed class ResourceItem
{
    public required string SourcePath { get; init; }
    public required string RelativePath { get; init; }
}

internal sealed class DiscoveredProject
{
    public required string Name { get; init; }
    public required string ProjectFilePath { get; init; }
    public required string ProjectDirectory { get; init; }
    public required ProjectKind Kind { get; init; }
    public required IReadOnlyList<string> ProjectReferences { get; init; }
    public required IReadOnlyList<ResourceItem> ResourceItems { get; init; }
}

internal sealed class ProjectGraph
{
    public required string RootProjectPath { get; init; }
    public required IReadOnlyList<DiscoveredProject> ProjectsInTopologicalOrder { get; init; }
}

internal static class ProjectDiscovery
{
    public static bool TryResolveProjectEntry(string source, out string projectFilePath)
    {
        projectFilePath = string.Empty;

        if (File.Exists(source))
        {
            var extension = Path.GetExtension(source);
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                projectFilePath = Path.GetFullPath(source);
                return true;
            }
        }

        if (!Directory.Exists(source))
        {
            return false;
        }

        var topLevelSolutions = Directory.GetFiles(source, "*.sln", SearchOption.TopDirectoryOnly);
        if (topLevelSolutions.Length == 1)
        {
            projectFilePath = Path.GetFullPath(topLevelSolutions[0]);
            return true;
        }

        var sameName = Path.GetFileName(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var preferredSolution = topLevelSolutions.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals(sameName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(preferredSolution))
        {
            projectFilePath = Path.GetFullPath(preferredSolution);
            return true;
        }

        var topLevelProjects = Directory.GetFiles(source, "*.csproj", SearchOption.TopDirectoryOnly);
        if (topLevelProjects.Length == 1)
        {
            projectFilePath = Path.GetFullPath(topLevelProjects[0]);
            return true;
        }

        var preferred = topLevelProjects.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals(sameName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(preferred))
        {
            projectFilePath = Path.GetFullPath(preferred);
            return true;
        }

        return false;
    }

    public static ProjectGraph LoadProjectGraph(string rootProjectPath)
    {
        var resolvedRootPath = Path.GetFullPath(rootProjectPath);
        var projectMap = new Dictionary<string, DiscoveredProject>(StringComparer.OrdinalIgnoreCase);

        if (Path.GetExtension(resolvedRootPath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var projectPath in GetSolutionProjectPaths(resolvedRootPath))
            {
                LoadRecursive(projectPath, projectMap);
            }
        }
        else
        {
            LoadRecursive(resolvedRootPath, projectMap);
        }

        var sorted = TopologicalSort(projectMap);

        return new ProjectGraph
        {
            RootProjectPath = resolvedRootPath,
            ProjectsInTopologicalOrder = sorted,
        };
    }

    private static IReadOnlyList<string> GetSolutionProjectPaths(string solutionPath)
    {
        if (!File.Exists(solutionPath))
        {
            return [];
        }

        var solutionDir = Path.GetDirectoryName(solutionPath) ?? string.Empty;
        var projectPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(solutionPath))
        {
            var match = Regex.Match(
                line,
                "^Project\\(\"[^\"]+\"\\)\\s*=\\s*\"[^\"]+\",\\s*\"([^\"]+\\.csproj)\",\\s*\"[^\"]+\"$",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var relativeProjectPath = match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var fullProjectPath = Path.GetFullPath(Path.Combine(solutionDir, relativeProjectPath));
            if (!File.Exists(fullProjectPath) || !seen.Add(fullProjectPath))
            {
                continue;
            }

            projectPaths.Add(fullProjectPath);
        }

        return projectPaths;
    }

    private static void LoadRecursive(string projectPath, Dictionary<string, DiscoveredProject> projectMap)
    {
        if (projectMap.ContainsKey(projectPath))
        {
            return;
        }

        if (!File.Exists(projectPath))
        {
            return;
        }

        var doc = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var projectDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        var projectReferences = doc
            .Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFullPath(Path.Combine(projectDir, v!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var packageReferences = doc
            .Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

        var isTestProject = IsTestProject(doc, projectName, packageReferences);
        var resources = GetCopyResources(doc, projectDir);

        projectMap[projectPath] = new DiscoveredProject
        {
            Name = projectName,
            ProjectFilePath = projectPath,
            ProjectDirectory = projectDir,
            Kind = isTestProject ? ProjectKind.Test : ProjectKind.Production,
            ProjectReferences = projectReferences,
            ResourceItems = resources,
        };

        foreach (var reference in projectReferences)
        {
            LoadRecursive(reference, projectMap);
        }
    }

    private static bool IsTestProject(XDocument projectDoc, string projectName, List<string> packageReferences)
    {
        var isTestProperty = projectDoc
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "IsTestProject")
            ?.Value;

        if (bool.TryParse(isTestProperty, out var explicitIsTest) && explicitIsTest)
        {
            return true;
        }

        if (packageReferences.Any(IsKnownTestPackage))
        {
            return true;
        }

        return projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownTestPackage(string packageName)
    {
        var name = packageName.ToLowerInvariant();
        return name.Contains("microsoft.net.test.sdk")
            || name.Contains("mstest")
            || name.Contains("xunit")
            || name.Contains("nunit");
    }

    private static List<ResourceItem> GetCopyResources(XDocument projectDoc, string projectDir)
    {
        var resources = new List<ResourceItem>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in projectDoc.Descendants().Where(e => e.Name.LocalName is "None" or "Content"))
        {
            var include = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update");
            if (string.IsNullOrWhiteSpace(include) || include.Contains('*'))
            {
                continue;
            }

            var copyBehavior = item.Elements().FirstOrDefault(e => e.Name.LocalName == "CopyToOutputDirectory")?.Value;
            if (string.IsNullOrWhiteSpace(copyBehavior) || copyBehavior.Equals("Never", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(projectDir, include));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            addedPaths.Add(fullPath);
            resources.Add(new ResourceItem
            {
                SourcePath = fullPath,
                RelativePath = include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar),
            });
        }

        return resources;
    }

    private static List<DiscoveredProject> TopologicalSort(Dictionary<string, DiscoveredProject> projectMap)
    {
        var result = new List<DiscoveredProject>();
        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in projectMap)
        {
            Visit(kvp.Key, projectMap, visited, result);
        }

        return result;
    }

    private static void Visit(
        string projectPath,
        Dictionary<string, DiscoveredProject> projectMap,
        Dictionary<string, int> visited,
        List<DiscoveredProject> result)
    {
        if (visited.TryGetValue(projectPath, out var state))
        {
            if (state == 2)
            {
                return;
            }

            if (state == 1)
            {
                return;
            }
        }

        visited[projectPath] = 1;

        if (projectMap.TryGetValue(projectPath, out var project))
        {
            foreach (var reference in project.ProjectReferences)
            {
                if (projectMap.ContainsKey(reference))
                {
                    Visit(reference, projectMap, visited, result);
                }
            }

            result.Add(project);
        }

        visited[projectPath] = 2;
    }
}
