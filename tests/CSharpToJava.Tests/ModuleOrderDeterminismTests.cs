using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

public class ModuleOrderDeterminismTests
{
    private static string ProjectPath(string name) => $@"C:\repo\{name}\{name}.csproj";

    private static ProjectGraph MakeGraph(params (string Name, string[] References)[] projects) => new()
    {
        RootProjectPath = ProjectPath(projects[0].Name),
        ProjectsInTopologicalOrder = projects
            .Select(p => new DiscoveredProject
            {
                Name = p.Name,
                ProjectFilePath = ProjectPath(p.Name),
                ProjectDirectory = $@"C:\repo\{p.Name}",
                Kind = ProjectKind.Production,
                ProjectReferences = p.References.Select(ProjectPath).ToArray(),
                ResourceItems = [],
            })
            .ToList(),
    };

    private static string[] BuildModuleNames(ProjectGraph graph) =>
        MultiModulePlanner.Build(graph, includeTests: false)
            .ModulesInBuildOrder
            .Select(m => m.Name)
            .ToArray();

    [Fact]
    public void Build_IndependentModules_AreCaseInsensitivelySorted_RegardlessOfDiscoveryOrder()
    {
        // Same set of independent projects, discovered in different orders.
        var orders = new[]
        {
            new[] { "ZebraLib", "alphaTool", "Beta.Core", "gamma-module" },
            new[] { "gamma-module", "Beta.Core", "alphaTool", "ZebraLib" },
            new[] { "alphaTool", "gamma-module", "ZebraLib", "Beta.Core" },
        };

        var runs = orders
            .Select(order => BuildModuleNames(MakeGraph(
                order.Select(name => (name, Array.Empty<string>())).ToArray())))
            .ToList();

        var expected = orders[0]
            .Select(MultiModulePlanner.NormalizeModuleName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.All(runs, names => Assert.Equal(expected, names));
    }

    [Fact]
    public void Build_DependentModules_KeepDependencyFirstOrder_RegardlessOfDiscoveryAndReferenceOrder()
    {
        // alpha-app depends on yankee-lib and zulu-lib; the dependency name
        // order and the discovery order both vary between runs.
        var variants = new[]
        {
            new (string Name, string[] References)[]
            {
                ("Xray.Lib", []),
                ("Yankee.Lib", []),
                ("Zulu.Lib", []),
                ("Alpha.App", ["Zulu.Lib", "Yankee.Lib"]),
                ("Bravo.App", ["Yankee.Lib"]),
            },
            new (string Name, string[] References)[]
            {
                ("Zulu.Lib", []),
                ("Alpha.App", ["Yankee.Lib", "Zulu.Lib"]),
                ("Bravo.App", ["Yankee.Lib"]),
                ("Yankee.Lib", []),
                ("Xray.Lib", []),
            },
            new (string Name, string[] References)[]
            {
                ("Yankee.Lib", []),
                ("Zulu.Lib", []),
                ("Bravo.App", ["Yankee.Lib"]),
                ("Xray.Lib", []),
                ("Alpha.App", ["Zulu.Lib", "Yankee.Lib"]),
            },
        };

        var runs = variants.Select(variant => BuildModuleNames(MakeGraph(variant))).ToList();

        var expected = new[] { "yankee-lib", "zulu-lib", "alpha-app", "bravo-app", "xray-lib" };
        Assert.All(runs, names => Assert.Equal(expected, names));

        var position = runs[0]
            .Select((name, index) => (name, index))
            .ToDictionary(entry => entry.name, entry => entry.index, StringComparer.OrdinalIgnoreCase);
        Assert.True(position["yankee-lib"] < position["alpha-app"]);
        Assert.True(position["zulu-lib"] < position["alpha-app"]);
        Assert.True(position["yankee-lib"] < position["bravo-app"]);
    }
}
