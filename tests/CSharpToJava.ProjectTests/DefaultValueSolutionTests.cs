using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class DefaultValueSolutionTests
{
    private static string SlnPath =>
        Path.Combine(AppContext.BaseDirectory, "SampleDefaultSolution", "SampleDefaultSolution.sln");

    [Fact]
    public async Task Solution_AllProjectsConvertedWithCorrectDefaults()
    {
        Assert.True(File.Exists(SlnPath), $"Solution not found: {SlnPath}");
        SolutionLoader.EnsureMSBuildRegistered();

        using var loader = new SolutionLoader();
        var projects = await loader.OpenSolutionAsync(SlnPath);
        Assert.Equal(2, projects.Count);

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var pipeline = new ConversionPipeline();

        foreach (var wp in projects)
        {
            // 把被引用项目的源加入语义编译，以解析跨项目类型（如 Lib 的 Point），
            // 但只 emit 当前项目自身的文件。
            var extraPaths = wp.ProjectReferences
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList()!;

            var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                wp.Directory,
                options,
                additionalSemanticProjectPaths: extraPaths,
                projectName: wp.Name,
                projectFilePath: wp.FilePath,
                projectReferences: wp.ProjectReferences,
                isTestProject: wp.IsTestProject);

            Assert.NotEmpty(results);
            foreach (var r in results)
            {
                Assert.True(r.Success, $"{wp.Name}: {string.Join("\n", r.Diagnostics)}");
                // 解决方案级不应残留 C# 语法
                Assert.DoesNotContain("=>", r.GeneratedCode);
                Assert.DoesNotContain("?.", r.GeneratedCode);
                Assert.DoesNotContain("??", r.GeneratedCode);
            }
        }

        // 抽样验证 Lib 的 Defaults.java（无项目引用，独立转换）
        var libDir = projects.Single(p => p.Name == "SampleDefaultLib").Directory;
        var libResults = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, options);
        var defaultsJava = libResults.Single(r => r.FileName == "Defaults.java");
        Assert.Contains("new Point()", defaultsJava.GeneratedCode);
        Assert.Contains("Status.Full", defaultsJava.GeneratedCode);
        Assert.Contains("Tuple.of(0, null)", defaultsJava.GeneratedCode);

        // 抽样验证 App 的 Program.java（引用 Lib；default 覆盖基本类型/可空/decimal/嵌套泛型）
        var app = projects.Single(p => p.Name == "SampleDefaultApp");
        var appDir = app.Directory;
        var appExtra = app.ProjectReferences
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList()!;
        var appResults = await pipeline.ConvertProjectWithPartialMergeAsync(
            appDir,
            options,
            additionalSemanticProjectPaths: appExtra,
            projectName: app.Name,
            projectFilePath: app.FilePath,
            projectReferences: app.ProjectReferences,
            isTestProject: app.IsTestProject);
        var programJava = appResults.Single(r => r.FileName == "Program.java");
        Assert.Contains("null", programJava.GeneratedCode);   // int? / string 等可空/引用
        Assert.Contains("Decimal.ZERO", programJava.GeneratedCode);
    }
}
