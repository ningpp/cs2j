using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class CrossProjectGenericClassTokenTests
{
    private static string SlnPath =>
        Path.Combine(AppContext.BaseDirectory, "SampleStaticGenericSolution", "SampleStaticGenericSolution.sln");

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    /// <summary>
    /// Lib project's generic method (GraphSort.Sort<TEdge>) uses LINQ
    /// (Select on IEnumerable<TEdge>) which gets rewritten to procedural
    /// LINQ methods. The procedural LINQ methods do NOT create TEdge[]
    /// arrays (they only iterate and yield), so neither Sort<TEdge> nor
    /// the procedural methods should gain a spurious Class<TEdge> parameter.
    ///
    /// Previously, ClassTransformer.DetectTypeOfDefaultUsage unconditionally
    /// forwarded explicit generic call type arguments (e.g. Sort_ProceduralLinq1<TEdge>),
    /// causing Sort<TEdge> to gain a Class<TEdge> parameter that the call site
    /// in another project could not detect (DetectDefaultUsageInMethod correctly
    /// checks MethodLikelyNeedsClassTokens before forwarding). This test verifies
    /// the two scanners are now consistent.
    /// </summary>
    [Fact]
    public async Task LibProject_GenericMethod_WithLinq_NoSpuriousClassToken()
    {
        var libDir = Path.Combine(AppContext.BaseDirectory, "SampleStaticGenericSolution", "SampleStaticGenericLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, Options);

        Assert.NotEmpty(results);
        var graphSort = Assert.Single(results, r =>
            string.Equals(r.FileName, "GraphSort.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(graphSort.Success, string.Join("\n", graphSort.Diagnostics));

        // The sort method must NOT have a Class<TEdge> parameter — the procedural
        // LINQ methods don't create TEdge[] arrays, so no Class token is needed.
        Assert.DoesNotContain("Class<TEdge>", graphSort.GeneratedCode);
        // The sort signature should be: sort(EdgeGraph<TEdge> graph)
        Assert.Contains("sort(EdgeGraph<TEdge> graph)", graphSort.GeneratedCode);
    }

    /// <summary>
    /// App project calls Lib's generic method GraphSort.Sort(graph) where
    /// graph is EdgeGraph<TestEdge>. Since Sort<TEdge> does NOT have a
    /// Class<TEdge> parameter, the call site must NOT inject TestEdge.class.
    ///
    /// This test ensures the call site and the method definition are consistent
    /// — both Lib (definition) and App (call site) agree that no Class token
    /// is needed.
    /// </summary>
    [Fact]
    public async Task AppProject_CrossProjectGenericCall_NoSpuriousClassToken()
    {
        var appDir = Path.Combine(AppContext.BaseDirectory, "SampleStaticGenericSolution", "SampleStaticGenericApp");
        var libDir = Path.Combine(AppContext.BaseDirectory, "SampleStaticGenericSolution", "SampleStaticGenericLib");
        Assert.True(Directory.Exists(appDir), $"Fixture not found: {appDir}");

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(
            appDir,
            Options,
            additionalSemanticProjectPaths: new[] { libDir });

        Assert.NotEmpty(results);
        var program = Assert.Single(results, r =>
            string.Equals(r.FileName, "Program.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(program.Success, string.Join("\n", program.Diagnostics));

        // The call to GraphSort.sort(graph) must NOT inject TestEdge.class —
        // the method signature has no Class<TEdge> parameter.
        Assert.DoesNotContain("TestEdge.class", program.GeneratedCode);
        // The call should be: GraphSort.sort(graph)
        Assert.Contains("GraphSort.sort(graph)", program.GeneratedCode);
    }

    /// <summary>
    /// Solution-level test: convert both Lib and App projects via SolutionLoader
    /// (MSBuild mode), which is the same path the real agl project uses.
    /// Both the Lib definition and the App call site must be consistent —
    /// neither should have a Class<TEdge> token.
    /// </summary>
    [Fact]
    public async Task SlnProject_CrossProjectGenericCall_NoSpuriousClassToken()
    {
        Assert.True(File.Exists(SlnPath), $"Solution not found: {SlnPath}");
        SolutionLoader.EnsureMSBuildRegistered();

        using var loader = new SolutionLoader();
        var projects = await loader.OpenSolutionAsync(SlnPath);
        Assert.Equal(2, projects.Count);

        var programJava = string.Empty;
        var graphSortJava = string.Empty;

        foreach (var wp in projects)
        {
            var extraPaths = wp.ProjectReferences
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList()!;

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                wp.Directory,
                Options,
                additionalSemanticProjectPaths: extraPaths,
                projectName: wp.Name,
                projectFilePath: wp.FilePath,
                projectReferences: wp.ProjectReferences,
                isTestProject: wp.IsTestProject);

            Assert.NotEmpty(results);
            foreach (var r in results)
            {
                Assert.True(r.Success, $"{wp.Name}/{r.FileName}: {string.Join("\n", r.Diagnostics)}");
            }

            var programResult = results.FirstOrDefault(r =>
                string.Equals(r.FileName, "Program.java", StringComparison.OrdinalIgnoreCase));
            if (programResult != null)
                programJava = programResult.GeneratedCode;

            var graphSortResult = results.FirstOrDefault(r =>
                string.Equals(r.FileName, "GraphSort.java", StringComparison.OrdinalIgnoreCase));
            if (graphSortResult != null)
                graphSortJava = graphSortResult.GeneratedCode;
        }

        // Lib: GraphSort.java must NOT have Class<TEdge> parameter
        Assert.DoesNotContain("Class<TEdge>", graphSortJava);

        // App: Program.java must NOT inject TestEdge.class
        Assert.DoesNotContain("TestEdge.class", programJava);
    }
}
