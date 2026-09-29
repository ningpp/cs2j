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

    /// <summary>
    /// Workspace (MSBuildWorkspace) path: the App project's compilation references
    /// the Lib project as a ProjectReference, so Lib symbols carry source syntax
    /// from the Lib compilation. When a call site in the App triggers
    /// DetectDefaultUsageInMethod on the recursive generic method, the invocation
    /// nodes belong to the Lib compilation's trees — the App semantic model cannot
    /// bind them, so GetSymbolInfo returns null. The lenient
    /// "explicitCallee == null" forwarding must NOT register Class&lt;T&gt; params
    /// for the method: the Lib phase already decided its signature has no Class
    /// slots, and spurious tokens at the App call site would break compilation.
    /// </summary>
    [Fact]
    public async Task WorkspaceAppProject_RecursiveGenericLibMethod_NoSpuriousClassToken()
    {
        Assert.True(File.Exists(SlnPath), $"Solution not found: {SlnPath}");
        SolutionLoader.EnsureMSBuildRegistered();

        using var loader = new SolutionLoader();
        var projects = await loader.OpenSolutionAsync(SlnPath);
        var appProject = Assert.Single(projects, p =>
            p.Name.StartsWith("SampleStaticGenericApp", StringComparison.OrdinalIgnoreCase));

        var emitFilePaths = new HashSet<string>(
            appProject.Documents
                .Where(d => d.FilePath != null)
                .Select(d => Path.GetFullPath(d.FilePath!)),
            StringComparer.OrdinalIgnoreCase);

        var pipeline = new ProjectConversionPipeline(Options);
        var results = await pipeline.ConvertProjectAsync(
            appProject.Compilation,
            emitFilePaths,
            appProject.Name,
            appProject.FilePath,
            appProject.ProjectReferences,
            appProject.IsTestProject);

        Assert.NotEmpty(results);
        var program = Assert.Single(results, r =>
            string.Equals(r.FileName, "Program.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(program.Success, string.Join("\n", program.Diagnostics));
        // The call RectNodeUtils.Cross<TestEdge, int>(...) must NOT inject class
        // literals — RectNodeUtils.Cross<T, P> has no Class<T> leading parameters.
        Assert.DoesNotContain("TestEdge.class", program.GeneratedCode);
        Assert.DoesNotContain("int.class", program.GeneratedCode);
        // The call should keep its two plain arguments.
        Assert.Contains("cross(rn, rn)", program.GeneratedCode);
    }

}
