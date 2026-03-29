using System.Xml.Linq;
using CSharpToJava.Workspace;

namespace CSharpToJava.CLI;

internal sealed class ProjectConversionExclusion
{
    public required string ProjectPath { get; init; }
    public required string ProjectName { get; init; }
    public required string Reason { get; init; }
}

internal static class ProjectConversionExclusionPlanner
{
    public static IReadOnlyDictionary<string, ProjectConversionExclusion> Plan(ProjectGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return PlanCore(graph.ProjectsInTopologicalOrder.Select(project =>
            new ProjectNodeInfo(project.ProjectFilePath, project.Name, project.ProjectReferences)));
    }

    public static IReadOnlyDictionary<string, ProjectConversionExclusion> Plan(IReadOnlyList<WorkspaceProject> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return PlanCore(projects.Select(project =>
            new ProjectNodeInfo(project.FilePath, project.Name, project.ProjectReferences)));
    }

    private static IReadOnlyDictionary<string, ProjectConversionExclusion> PlanCore(IEnumerable<ProjectNodeInfo> projects)
    {
        var nodes = projects
            .Select(project => new ProjectNodeInfo(
                Path.GetFullPath(project.ProjectPath),
                project.ProjectName,
                project.ProjectReferences.Select(Path.GetFullPath).ToList()))
            .ToDictionary(project => project.ProjectPath, StringComparer.OrdinalIgnoreCase);

        var reverseDependencies = nodes.Values.ToDictionary(
            project => project.ProjectPath,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var project in nodes.Values)
        {
            foreach (var dependencyPath in project.ProjectReferences)
            {
                if (reverseDependencies.TryGetValue(dependencyPath, out var dependents))
                {
                    dependents.Add(project.ProjectPath);
                }
            }
        }

        var exclusions = new Dictionary<string, ProjectConversionExclusion>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        foreach (var project in nodes.Values)
        {
            var evaluation = EvaluateProjectSupport(project.ProjectPath);
            if (evaluation.IsSupported)
            {
                continue;
            }

            exclusions[project.ProjectPath] = new ProjectConversionExclusion
            {
                ProjectPath = project.ProjectPath,
                ProjectName = project.ProjectName,
                Reason = evaluation.Reason,
            };

            queue.Enqueue(project.ProjectPath);
        }

        while (queue.Count > 0)
        {
            var excludedProjectPath = queue.Dequeue();
            if (!reverseDependencies.TryGetValue(excludedProjectPath, out var dependents))
            {
                continue;
            }

            foreach (var dependentPath in dependents)
            {
                if (exclusions.ContainsKey(dependentPath) || !nodes.TryGetValue(dependentPath, out var dependentProject))
                {
                    continue;
                }

                exclusions[dependentPath] = new ProjectConversionExclusion
                {
                    ProjectPath = dependentPath,
                    ProjectName = dependentProject.ProjectName,
                    Reason = $"Depends on excluded project '{nodes[excludedProjectPath].ProjectName}'",
                };

                queue.Enqueue(dependentPath);
            }
        }

        return exclusions;
    }

    private static ProjectSupportEvaluation EvaluateProjectSupport(string projectPath)
    {
        try
        {
            var doc = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            var reasons = new List<string>();

            var sdk = doc.Root?.Attribute("Sdk")?.Value;
            if (!string.IsNullOrWhiteSpace(sdk)
                && sdk.Contains("Microsoft.NET.Sdk.WindowsDesktop", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("WindowsDesktop SDK");
            }

            if (GetBooleanProperty(doc, "UseWPF"))
            {
                reasons.Add("UseWPF=true");
            }

            if (GetBooleanProperty(doc, "UseWindowsForms"))
            {
                reasons.Add("UseWindowsForms=true");
            }

            var targetFrameworks = GetPropertyValues(doc, "TargetFramework")
                .Concat(GetPropertyValues(doc, "TargetFrameworks")
                    .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var windowsTargetFramework = targetFrameworks.FirstOrDefault(IsWindowsOnlyTargetFramework);
            if (!string.IsNullOrWhiteSpace(windowsTargetFramework))
            {
                reasons.Add($"TargetFramework={windowsTargetFramework}");
            }

            var targetPlatformIdentifier = GetPropertyValues(doc, "TargetPlatformIdentifier")
                .FirstOrDefault(IsWindowsOnlyTargetPlatformIdentifier);
            if (!string.IsNullOrWhiteSpace(targetPlatformIdentifier))
            {
                reasons.Add("TargetPlatformIdentifier=" + targetPlatformIdentifier);
            }

            var usesUwpPackage = doc
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Any(value => string.Equals(value, "Microsoft.NETCore.UniversalWindowsPlatform", StringComparison.OrdinalIgnoreCase));
            if (usesUwpPackage)
            {
                reasons.Add("UniversalWindowsPlatform package");
            }

            if (reasons.Count == 0)
            {
                return ProjectSupportEvaluation.Supported;
            }

            return new ProjectSupportEvaluation(false, "Windows-only project: " + string.Join(", ", reasons));
        }
        catch (Exception ex)
        {
            return new ProjectSupportEvaluation(false, $"Failed to inspect project file: {ex.Message}");
        }
    }

    private static IEnumerable<string> GetPropertyValues(XDocument projectDoc, string propertyName)
    {
        return projectDoc
            .Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => element.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
    }

    private static bool GetBooleanProperty(XDocument projectDoc, string propertyName)
    {
        return GetPropertyValues(projectDoc, propertyName)
            .Any(value => bool.TryParse(value, out var result) && result);
    }

    private static bool IsWindowsOnlyTargetFramework(string tfm)
    {
        return tfm.Contains("-windows", StringComparison.OrdinalIgnoreCase)
            || tfm.StartsWith("uap", StringComparison.OrdinalIgnoreCase)
            || tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase) && tfm.Contains("windows", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsOnlyTargetPlatformIdentifier(string identifier)
    {
        return identifier.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || identifier.Equals("UAP", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProjectNodeInfo(string ProjectPath, string ProjectName, IReadOnlyList<string> ProjectReferences);

    private sealed record ProjectSupportEvaluation(bool IsSupported, string Reason)
    {
        public static ProjectSupportEvaluation Supported { get; } = new(true, string.Empty);
    }
}