using CSharpToJava.CLI;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.ProjectTests;

[Collection("UnsignedTypePreprocessSerial")]
public class UnsignedConversionEndToEndTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleUnsignedSolution");

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [Fact]
    public async Task PreprocessedSolution_ConvertsToJava_WithCompatUnsignedMath()
    {
        var outputDir = Path.Combine(FixtureDir, ".test-output-e2e");

        try
        {
            // 1) 预处理：释放 Cs2jUnsignedMath + 注入 ProjectReference
            var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = Path.Combine(FixtureDir, "SampleUnsignedSolution.sln"),
                    DestinationRoot = outputDir,
                    Verbose = false,
                });
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            // 2) 主转换加载（workspace 路径）+ 过滤内部支撑库（与 Program.cs 同规则）
            SolutionLoader.EnsureMSBuildRegistered();
            using var loader = new SolutionLoader();
            var workspaceProjects = await loader.TryOpenAsync(result.PreprocessedSourcePath);
            Assert.NotNull(workspaceProjects);
            Assert.True(workspaceProjects.Count > 0);

            // 前置验证锁定：host compilation 必须能解析辅助类型（MSBuildWorkspace
            // 沿 sln 外 ProjectReference 加载 Cs2jUnsignedMath）。
            var libProject = workspaceProjects!.Single(p => p.Name == "SampleUnsignedLib");
            Assert.NotNull(libProject.Compilation.GetTypeByMetadataName("Cs2jUnsigned.UnsignedMath"));

            var convertProjects = workspaceProjects
                .Where(p => !Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.FilePath))
                .ToList();
            Assert.DoesNotContain(convertProjects, p => p.Name == "Cs2jUnsignedMath");

            // 3) 逐项目转换（支撑库仅作符号来源，见 additionalSemanticProjectPaths 说明）
            var pipeline = new ConversionPipeline();
            var converted = new List<ConversionResult>();
            foreach (var wp in convertProjects.Where(p => !p.IsTestProject))
            {
                // 支撑库仅作符号来源：其源码进入编译（等价于真实流程中 workspace
                // 编译携带的 ProjectReference），但 emit 集不含它，不会被转换。
                converted.AddRange(await pipeline.ConvertProjectWithPartialMergeAsync(
                    wp.Directory,
                    Options,
                    additionalSemanticProjectPaths: new[] { Path.Combine(result.IntermediateRoot, "Cs2jUnsignedMath") },
                    projectName: wp.Name,
                    projectFilePath: wp.FilePath,
                    projectReferences: wp.ProjectReferences,
                    isTestProject: wp.IsTestProject));
            }

            Assert.NotEmpty(converted);
            foreach (var r in converted)
            {
                Assert.True(r.Success, $"{r.FileName}: {string.Join("\n", r.Diagnostics)}");
            }

            // 4) 不再生成 Cs2jUnsigned/UnsignedMath.java（任何模块）
            Assert.DoesNotContain(converted, r =>
                string.Equals(r.FileName, "UnsignedMath.java", StringComparison.OrdinalIgnoreCase));

            // 5) 调用点带 compat import、camelCase 方法、无 Cs2jUnsigned 残留
            var operators = converted.Single(r =>
                string.Equals(r.FileName, "UnsignedOperators.java", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("import io.github.ningpp.compat.UnsignedMath;", operators.GeneratedCode);
            Assert.Contains("UnsignedMath.addUInt32(", operators.GeneratedCode);
            Assert.Contains("UnsignedMath.compareUInt32(", operators.GeneratedCode);
            Assert.DoesNotContain("Cs2jUnsigned.UnsignedMath", operators.GeneratedCode);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }
}
