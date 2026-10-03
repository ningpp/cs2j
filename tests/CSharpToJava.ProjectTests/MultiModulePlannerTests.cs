using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing;

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

    [Fact]
    public void Build_FiltersCs2jPrelude_AndDropsItsDependencyEdges()
    {
        // C19 M3 (appendix ruling 7): the delegate/equality-concat injected
        // Cs2jPrelude project is symbol-source only and must never become a
        // Maven module.
        var graph = new ProjectGraph
        {
            RootProjectPath = @"C:\repo\App\App.csproj",
            ProjectsInTopologicalOrder =
            [
                MakeProject(
                    "Cs2jPrelude",
                    @"C:\repo\App\Cs2jPrelude\Cs2jPrelude.csproj"),
                MakeProject(
                    "App",
                    @"C:\repo\App\App.csproj",
                    @"C:\repo\App\Cs2jPrelude\Cs2jPrelude.csproj"),
            ],
        };

        var plan = MultiModulePlanner.Build(graph, includeTests: false);

        Assert.Single(plan.ModulesInBuildOrder);
        Assert.Equal("app", plan.ModulesInBuildOrder[0].Name);
        Assert.Empty(plan.ModulesInBuildOrder[0].CompileDependencies);
    }

    [Fact]
    public void IsExecutedSupportLibraryProject_RecognizesCs2jPreludeOnlyWhenPreludePassRan()
    {
        // C19 M3 (appendix ruling 7): recognition is gated on the executed set.
        var withPrelude = new HashSet<PassSupportLibrary> { PassSupportLibrary.Cs2jPrelude };
        Assert.True(PassChainFactory.IsExecutedSupportLibraryProject(
            withPrelude,
            @"C:\repo\App\Cs2jPrelude\Cs2jPrelude.csproj"));

        var withoutPrelude = new HashSet<PassSupportLibrary> { PassSupportLibrary.Cs2jArray };
        Assert.False(PassChainFactory.IsExecutedSupportLibraryProject(
            withoutPrelude,
            @"C:\repo\App\Cs2jPrelude\Cs2jPrelude.csproj"));

        // User projects are never support libraries.
        Assert.False(PassChainFactory.IsExecutedSupportLibraryProject(
            withPrelude,
            @"C:\repo\App\App.csproj"));
    }
}
