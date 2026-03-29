using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

public class ProjectDiscoveryTests
{
    [Fact]
    public void TryResolveProjectEntry_PrefersTopLevelSolution_ForDirectorySource()
    {
        var root = CreateTempDirectory();
        try
        {
            var slnPath = Path.Combine(root, "GraphLayout.sln");
            File.WriteAllText(slnPath, string.Empty);
            File.WriteAllText(Path.Combine(root, "GraphLayout.csproj"), "<Project />");

            var resolved = ProjectDiscovery.TryResolveProjectEntry(root, out var entryPath);

            Assert.True(resolved);
            Assert.Equal(Path.GetFullPath(slnPath), entryPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadProjectGraph_LoadsProjectsFromSolutionAndReferences()
    {
        var root = CreateTempDirectory();
        try
        {
            var libProject = CreateProject(root, "Lib", isTest: false);
            var appProject = CreateProject(root, "App", isTest: false, projectReferences: [libProject]);
            var testProject = CreateProject(root, "App.Tests", isTest: true, projectReferences: [appProject]);
            var solutionPath = CreateSolution(root, "GraphLayout.sln", [libProject, appProject, testProject]);

            var graph = ProjectDiscovery.LoadProjectGraph(solutionPath);

            Assert.Equal(Path.GetFullPath(solutionPath), graph.RootProjectPath);
            Assert.Equal(3, graph.ProjectsInTopologicalOrder.Count);
            Assert.Collection(
                graph.ProjectsInTopologicalOrder,
                project =>
                {
                    Assert.Equal("Lib", project.Name);
                    Assert.Equal(ProjectKind.Production, project.Kind);
                },
                project =>
                {
                    Assert.Equal("App", project.Name);
                    Assert.Equal(ProjectKind.Production, project.Kind);
                    Assert.Single(project.ProjectReferences);
                    Assert.Equal(Path.GetFullPath(libProject), project.ProjectReferences[0]);
                },
                project =>
                {
                    Assert.Equal("App.Tests", project.Name);
                    Assert.Equal(ProjectKind.Test, project.Kind);
                    Assert.Single(project.ProjectReferences);
                    Assert.Equal(Path.GetFullPath(appProject), project.ProjectReferences[0]);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectConversionExclusionPlanner_ExcludesWindowsProjects_AndTheirDependents()
    {
        var root = CreateTempDirectory();
        try
        {
            var coreProject = CreateProject(root, "Core", isTest: false);
            var graphmapsProject = CreateProject(
                root,
                "GraphmapsWpfControl",
                isTest: false,
                projectReferences: [coreProject],
                sdk: "Microsoft.NET.Sdk.WindowsDesktop",
                targetFramework: "net8.0-windows",
                useWpf: true);
            var consumerProject = CreateProject(root, "Consumer", isTest: false, projectReferences: [graphmapsProject]);
            var solutionPath = CreateSolution(root, "GraphLayout.sln", [coreProject, graphmapsProject, consumerProject]);

            var graph = ProjectDiscovery.LoadProjectGraph(solutionPath);
            var exclusions = ProjectConversionExclusionPlanner.Plan(graph);

            Assert.DoesNotContain(Path.GetFullPath(coreProject), exclusions.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFullPath(graphmapsProject), exclusions.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFullPath(consumerProject), exclusions.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("Windows-only project", exclusions[Path.GetFullPath(graphmapsProject)].Reason, StringComparison.Ordinal);
            Assert.Contains("Depends on excluded project 'GraphmapsWpfControl'", exclusions[Path.GetFullPath(consumerProject)].Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectConversionExclusionPlanner_ExcludesUwpProjects()
    {
        var root = CreateTempDirectory();
        try
        {
            var coreProject = CreateProject(root, "Core", isTest: false);
            var uwpProject = CreateProject(
                root,
                "UwpGraphControl",
                isTest: false,
                projectReferences: [coreProject],
                targetFramework: "net8.0",
                targetPlatformIdentifier: "UAP",
                packageReferences: ["Microsoft.NETCore.UniversalWindowsPlatform"]);
            var solutionPath = CreateSolution(root, "GraphLayout.sln", [coreProject, uwpProject]);

            var graph = ProjectDiscovery.LoadProjectGraph(solutionPath);
            var exclusions = ProjectConversionExclusionPlanner.Plan(graph);

            Assert.Contains(Path.GetFullPath(uwpProject), exclusions.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("TargetPlatformIdentifier=UAP", exclusions[Path.GetFullPath(uwpProject)].Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-project-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateProject(
        string root,
        string name,
        bool isTest,
        IReadOnlyList<string>? projectReferences = null,
        string sdk = "Microsoft.NET.Sdk",
        string targetFramework = "net10.0",
        bool useWpf = false,
        bool useWindowsForms = false,
        string? targetPlatformIdentifier = null,
        IReadOnlyList<string>? packageReferences = null)
    {
        var projectDir = Path.Combine(root, name);
        Directory.CreateDirectory(projectDir);

        var referencesXml = string.Join(Environment.NewLine,
            (projectReferences ?? [])
                .Select(reference =>
                {
                    var relativePath = Path.GetRelativePath(projectDir, reference).Replace(Path.DirectorySeparatorChar, '\\');
                    return $"    <ProjectReference Include=\"{relativePath}\" />";
                }));

        var isTestProperty = isTest ? "    <IsTestProject>true</IsTestProject>" + Environment.NewLine : string.Empty;
                var useWpfProperty = useWpf ? "    <UseWPF>true</UseWPF>" + Environment.NewLine : string.Empty;
                var useWindowsFormsProperty = useWindowsForms ? "    <UseWindowsForms>true</UseWindowsForms>" + Environment.NewLine : string.Empty;
                var targetPlatformIdentifierProperty = string.IsNullOrWhiteSpace(targetPlatformIdentifier)
                        ? string.Empty
                        : $"    <TargetPlatformIdentifier>{targetPlatformIdentifier}</TargetPlatformIdentifier>" + Environment.NewLine;
        var itemGroup = string.IsNullOrEmpty(referencesXml)
            ? string.Empty
            : $@"
  <ItemGroup>
{referencesXml}
  </ItemGroup>";

                var packageReferencesXml = string.Join(Environment.NewLine,
                        (packageReferences ?? [])
                                .Select(packageReference => $"    <PackageReference Include=\"{packageReference}\" Version=\"1.0.0\" />"));
                var packageItemGroup = string.IsNullOrEmpty(packageReferencesXml)
                        ? string.Empty
                        : $@"
    <ItemGroup>
{packageReferencesXml}
    </ItemGroup>";

                var content = $@"<Project Sdk=""{sdk}"">
  <PropertyGroup>
        <TargetFramework>{targetFramework}</TargetFramework>
{isTestProperty}{useWpfProperty}{useWindowsFormsProperty}{targetPlatformIdentifierProperty}  </PropertyGroup>{itemGroup}{packageItemGroup}
</Project>";

        var projectPath = Path.Combine(projectDir, name + ".csproj");
        File.WriteAllText(projectPath, content);
        return projectPath;
    }

    private static string CreateSolution(string root, string fileName, IReadOnlyList<string> projectPaths)
    {
        var lines = new List<string>
        {
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17",
        };

        foreach (var projectPath in projectPaths)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var relativePath = Path.GetRelativePath(root, projectPath).Replace(Path.DirectorySeparatorChar, '\\');
            lines.Add($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projectName}\", \"{relativePath}\", \"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}\"");
            lines.Add("EndProject");
        }

        var solutionPath = Path.Combine(root, fileName);
        File.WriteAllLines(solutionPath, lines);
        return solutionPath;
    }
}