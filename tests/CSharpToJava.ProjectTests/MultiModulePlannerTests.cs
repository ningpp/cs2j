using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class MultiModulePlannerTests
{
    private static DiscoveredProject MakeProject(
        string name, string path, params string[] references) => new()
    {
        Name = name,
        ProjectFilePath = path,
        ProjectDirectory = Path.GetDirectoryName(path)!,
        Kind = ProjectKind.Production,
        ProjectReferences = references,
        ResourceItems = [],
    };

    [Fact]
    public void Build_FiltersCs2jUnsignedMath_AndDropsItsDependencyEdges()
    {
        var graph = new ProjectGraph
        {
            RootProjectPath = @"C:\repo\App\App.csproj",
            ProjectsInTopologicalOrder =
            [
                MakeProject(
                    "Cs2jUnsignedMath",
                    @"C:\repo\App\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj"),
                MakeProject(
                    "SampleUnsignedLib",
                    @"C:\repo\App\SampleUnsignedLib\SampleUnsignedLib.csproj",
                    @"C:\repo\App\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj"),
                MakeProject(
                    "App",
                    @"C:\repo\App\App.csproj",
                    @"C:\repo\App\SampleUnsignedLib\SampleUnsignedLib.csproj",
                    @"C:\repo\App\Cs2jUnsignedMath\Cs2jUnsignedMath.csproj"),
            ],
        };

        var plan = MultiModulePlanner.Build(graph, includeTests: false);

        Assert.Equal(2, plan.ModulesInBuildOrder.Count);
        var app = plan.ModulesInBuildOrder.Single(m => m.Name == "app");
        var lib = plan.ModulesInBuildOrder.Single(m => m.Name == "sampleunsignedlib");
        Assert.Equal("sampleunsignedlib", string.Join(",", app.CompileDependencies));
        Assert.Empty(lib.CompileDependencies);
    }

    [Fact]
    public void Build_FiltersCs2jArray_AndDropsItsDependencyEdges()
    {
        var graph = new ProjectGraph
        {
            RootProjectPath = @"C:\repo\App\App.csproj",
            ProjectsInTopologicalOrder =
            [
                MakeProject(
                    "Cs2jArray",
                    @"C:\repo\App\Cs2jArray\Cs2jArray.csproj"),
                MakeProject(
                    "SampleArrayLib",
                    @"C:\repo\App\SampleArrayLib\SampleArrayLib.csproj",
                    @"C:\repo\App\Cs2jArray\Cs2jArray.csproj"),
                MakeProject(
                    "App",
                    @"C:\repo\App\App.csproj",
                    @"C:\repo\App\SampleArrayLib\SampleArrayLib.csproj",
                    @"C:\repo\App\Cs2jArray\Cs2jArray.csproj"),
            ],
        };

        var plan = MultiModulePlanner.Build(graph, includeTests: false);

        Assert.Equal(2, plan.ModulesInBuildOrder.Count);
        var app = plan.ModulesInBuildOrder.Single(m => m.Name == "app");
        var lib = plan.ModulesInBuildOrder.Single(m => m.Name == "samplearraylib");
        Assert.Equal("samplearraylib", string.Join(",", app.CompileDependencies));
        Assert.Empty(lib.CompileDependencies);
    }
}
