using System.Xml.Linq;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.CLI;

internal sealed class ProjectConversionExclusion
{
    public required string ProjectPath { get; init; }
    public required string ProjectName { get; init; }
    public required string Reason { get; init; }
}

internal static class ProjectConversionExclusionPlanner
{
    /// <summary>
    /// AssemblyNames that are explicitly allowed to be converted even though they
    /// would otherwise be excluded by the platform rules (e.g. a WPF control that
    /// has a working cs2j WPF compatibility layer). Matched exactly (case-sensitive)
    /// against the csproj <c>AssemblyName</c> property — never against disk paths.
    /// </summary>
    private static readonly IReadOnlySet<string> HARD_CODE_ASSEMBLE_NAMES = new HashSet<string>(
        new string[]
        {
            "Microsoft.Msagl.WpfGraphControl",
            "Microsoft.Msagl.GraphmapsWpfControl",
            "Microsoft.Msagl.GraphViewerGdi",
            "TestGraphmaps",
        },
        StringComparer.Ordinal);

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
            .GroupBy(project => project.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
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

            var useWpf = GetBooleanProperty(doc, "UseWPF");
            if (useWpf)
            {
                reasons.Add("UseWPF=true");
            }

            if (GetBooleanProperty(doc, "UseWindowsForms"))
            {
                reasons.Add("UseWindowsForms=true");
            }

            // GenAPI-style ref assemblies (e.g. dotnet/runtime ref\*.csproj) are
            // API-surface stubs: every public type is re-declared with `throw null`
            // bodies. Converting them duplicates all Java types, and merging them
            // into the semantic compilation corrupts DeclaringSyntaxReferences of
            // partial types (cross-tree references crash the converters).
            var assemblyName = GetPropertyValues(doc, "AssemblyName").FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(assemblyName)
                && assemblyName.EndsWith(".Ref", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"Ref assembly (AssemblyName={assemblyName})");
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

            // Explicit allow-list: specific projects (e.g. a WPF control backed by the
            // cs2j WPF compatibility layer) are converted even though they match the
            // Windows-only heuristics above. Matched purely by AssemblyName, never by path.
            if (!string.IsNullOrWhiteSpace(assemblyName) && HARD_CODE_ASSEMBLE_NAMES.Contains(assemblyName))
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