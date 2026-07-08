using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class RefOutInProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleRefOutInSolution");

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [Fact]
    public async Task LibProject_RefOutInMethods_ConvertCorrectly()
    {
        var libDir = Path.Combine(FixtureDir, "SampleRefOutInLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, Options);

        Assert.NotEmpty(results);

        // Calculator.java — ref int params become IntHolder, out Point becomes ObjectHolder<Point>
        var calculator = Assert.Single(results, r =>
            string.Equals(r.FileName, "Calculator.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(calculator.Success, string.Join("\n", calculator.Diagnostics));
        Assert.Contains("IntHolder", calculator.GeneratedCode);
        Assert.Contains("ObjectHolder", calculator.GeneratedCode);

        // GenericHolder.java — ref T becomes ObjectHolder<T>
        var genericHolder = Assert.Single(results, r =>
            string.Equals(r.FileName, "GenericHolder.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(genericHolder.Success, string.Join("\n", genericHolder.Diagnostics));
        Assert.Contains("ObjectHolder", genericHolder.GeneratedCode);
    }

    [Fact]
    public async Task AppProject_RefCallAcrossProjects_GeneratesHolderAtCallSite()
    {
        var appDir = Path.Combine(FixtureDir, "SampleRefOutInApp");
        var libDir = Path.Combine(FixtureDir, "SampleRefOutInLib");
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

        // At the call site, ref int args require IntHolder, out var p requires ObjectHolder
        Assert.Contains("IntHolder", program.GeneratedCode);
        Assert.Contains("ObjectHolder", program.GeneratedCode);
    }

    [Fact]
    public async Task SlnProject_FullConversion_AllFilesSucceed()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleRefOutIn.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");
        SolutionLoader.EnsureMSBuildRegistered();

        using var loader = new SolutionLoader();
        var projects = await loader.OpenSolutionAsync(slnPath);
        Assert.Equal(2, projects.Count);

        var pipeline = new ConversionPipeline();

        foreach (var wp in projects)
        {
            var extraPaths = wp.ProjectReferences
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList()!;

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
        }
    }

    [Fact]
    public async Task SlnProject_RefOutStruct_GeneratesObjectHolder()
    {
        var libDir = Path.Combine(FixtureDir, "SampleRefOutInLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, Options);

        Assert.NotEmpty(results);
        var calculator = Assert.Single(results, r =>
            string.Equals(r.FileName, "Calculator.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(calculator.Success, string.Join("\n", calculator.Diagnostics));

        // out Point (struct) should generate ObjectHolder<Point>
        Assert.Contains("ObjectHolder", calculator.GeneratedCode);
    }
}
