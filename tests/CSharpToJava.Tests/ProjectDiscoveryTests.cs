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

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-project-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateProject(string root, string name, bool isTest, IReadOnlyList<string>? projectReferences = null)
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
        var itemGroup = string.IsNullOrEmpty(referencesXml)
            ? string.Empty
            : $@"
  <ItemGroup>
{referencesXml}
  </ItemGroup>";

                var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
{isTestProperty}  </PropertyGroup>{itemGroup}
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