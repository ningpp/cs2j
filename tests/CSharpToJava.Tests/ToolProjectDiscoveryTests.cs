using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

public class ToolProjectDiscoveryTests
{
    [Fact]
    public void LoadProjectGraph_ToolPathProject_IsClassifiedAsToolAndExcludedFromModules()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "c2j-tool-discovery-" + Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(tempRoot, "src", "App");
        var toolDir = Path.Combine(tempRoot, "tools", "Dot2Graph");

        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(toolDir);

        try
        {
            File.WriteAllText(Path.Combine(appDir, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../../tools/Dot2Graph/Dot2Graph.csproj" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(toolDir, "Dot2Graph.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var graph = ProjectDiscovery.LoadProjectGraph(Path.Combine(appDir, "App.csproj"));

            var toolProject = graph.ProjectsInTopologicalOrder.Single(p => p.Name == "Dot2Graph");
            Assert.Equal(ProjectKind.Tool, toolProject.Kind);

            var plan = MultiModulePlanner.Build(graph, includeTests: true);
            Assert.DoesNotContain(plan.ModulesInBuildOrder, m =>
                m.Assignments.Any(a => a.Project.Name == "Dot2Graph"));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
