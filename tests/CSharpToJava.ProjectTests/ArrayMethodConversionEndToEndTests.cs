using CSharpToJava.CLI;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.ProjectTests;

[Collection("ArrayMethodPreprocessSerial")]
public class ArrayMethodConversionEndToEndTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleArraySolution");

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [Fact]
    public async Task PreprocessedSolution_ConvertsToJava_WithCompatArrayHelper()
    {
        var outputDir = Path.Combine(FixtureDir, ".test-output-e2e");

        try
        {
            // 1) 预处理：释放 Cs2jArray + 注入 ProjectReference
            var result = await ProjectArrayMethodPreprocessor.PreprocessAsync(
                new ProjectArrayMethodPreprocessRequest
                {
                    SourcePath = Path.Combine(FixtureDir, "SampleArray.sln"),
                    DestinationRoot = outputDir,
                    Verbose = false,
                });
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            // 2) 主转换加载
            SolutionLoader.EnsureMSBuildRegistered();
            using var loader = new SolutionLoader();
            var workspaceProjects = await loader.TryOpenAsync(result.PreprocessedSourcePath);
            Assert.NotNull(workspaceProjects);
            Assert.True(workspaceProjects.Count > 0);

            // 3) 过滤内部支撑库
            var convertProjects = workspaceProjects
                .Where(p => !Cs2jArrayProject.IsCs2jArrayProject(p.FilePath))
                .ToList();
            Assert.DoesNotContain(convertProjects, p => p.Name == "Cs2jArray");

            // 4) 逐项目转换（支撑库仅作符号来源）
            var pipeline = new ConversionPipeline();
            var converted = new List<ConversionResult>();
            foreach (var wp in convertProjects.Where(p => !p.IsTestProject))
            {
                converted.AddRange(await pipeline.ConvertProjectWithPartialMergeAsync(
                    wp.Directory, Options,
                    additionalSemanticProjectPaths: convertProjects.Select(p => p.Directory),
                    projectName: wp.Name,
                    projectFilePath: wp.FilePath));
            }

            // 5) 产物断言
            Assert.True(converted.Count > 0);
            var libJava = converted.FirstOrDefault(c => c.FileName.Contains("ArrayOperations"));
            Assert.NotNull(libJava);
            Assert.Contains("ArrayHelper", libJava.GeneratedCode);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
