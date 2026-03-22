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

        if (File.Exists(source) && Path.GetExtension(source).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            projectFilePath = Path.GetFullPath(source);
            return true;
        }

        if (!Directory.Exists(source))
        {
            return false;
        }

        var topLevelProjects = Directory.GetFiles(source, "*.csproj", SearchOption.TopDirectoryOnly);
        if (topLevelProjects.Length == 1)
        {
            projectFilePath = Path.GetFullPath(topLevelProjects[0]);
            return true;
        }

        var sameName = Path.GetFileName(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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
        var projectMap = new Dictionary<string, DiscoveredProject>(StringComparer.OrdinalIgnoreCase);
        LoadRecursive(Path.GetFullPath(rootProjectPath), projectMap);

        var sorted = TopologicalSort(projectMap);

        return new ProjectGraph
        {
            RootProjectPath = Path.GetFullPath(rootProjectPath),
            ProjectsInTopologicalOrder = sorted,
        };
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
        var isToolProject = IsToolProject(projectPath, projectName);
        var resources = GetCopyResources(doc, projectDir);

        var kind = ProjectKind.Production;
        if (isToolProject)
        {
            kind = ProjectKind.Tool;
        }
        else if (isTestProject)
        {
            kind = ProjectKind.Test;
        }

        projectMap[projectPath] = new DiscoveredProject
        {
            Name = projectName,
            ProjectFilePath = projectPath,
            ProjectDirectory = projectDir,
            Kind = kind,
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

    private static bool IsToolProject(string projectPath, string projectName)
    {
        var normalizedPath = Path.GetFullPath(projectPath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        var toolsSegment = $"{Path.DirectorySeparatorChar}tools{Path.DirectorySeparatorChar}";
        if (normalizedPath.Contains(toolsSegment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return projectName.EndsWith(".Tool", StringComparison.OrdinalIgnoreCase)
            || projectName.EndsWith(".Tools", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ResourceItem> GetCopyResources(XDocument projectDoc, string projectDir)
    {
        var resources = new List<ResourceItem>();

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
