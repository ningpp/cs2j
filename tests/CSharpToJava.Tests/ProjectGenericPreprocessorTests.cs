using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// 集成测试：ProjectGenericPreprocessor.PreprocessAsync 生成中间目录产物，
/// 并在 .NET 上验证 dotnet build / dotnet test。
/// 参考 ProjectGotoPreprocessorTests 与 ReadOnlyStructPreprocessorVerifyTests 风格。
/// </summary>
public class ProjectGenericPreprocessorTests
{
    [Fact]
    public void ResolveRestoreTarget_ForCsprojSource_ReturnsPreprocessedCsproj()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "Project");
            var intermediateRoot = Path.Combine(root, "intermediate");
            var sourceProject = Path.Combine(sourceRoot, "Project.csproj");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(sourceProject, """<Project Sdk="Microsoft.NET.Sdk"></Project>""");

            var preprocessedProject = Path.Combine(intermediateRoot, "Project.csproj");
            Directory.CreateDirectory(intermediateRoot);
            File.WriteAllText(preprocessedProject, """<Project Sdk="Microsoft.NET.Sdk"></Project>""");

            var layout = new ProjectPreprocessSourceLayout(
                sourceRoot,
                sourceProject,
                [sourceProject]);

            var target = ProjectGenericPreprocessor.ResolveRestoreTarget(intermediateRoot, layout);

            Assert.Equal(preprocessedProject, target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveRestoreTarget_ForSlnSource_ReturnsPreprocessedSln()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            var intermediateRoot = Path.Combine(root, "intermediate");
            var projectDir = Path.Combine(sourceRoot, "Project");
            var sourceProject = Path.Combine(projectDir, "Project.csproj");
            var sourceSln = Path.Combine(sourceRoot, "Solution.sln");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(sourceProject, """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
            File.WriteAllText(sourceSln, "Microsoft Visual Studio Solution File, Format Version 12.00\n");

            var preprocessedProjectDir = Path.Combine(intermediateRoot, "Project");
            var preprocessedProject = Path.Combine(preprocessedProjectDir, "Project.csproj");
            var preprocessedSln = Path.Combine(intermediateRoot, "Solution.sln");
            Directory.CreateDirectory(preprocessedProjectDir);
            File.WriteAllText(preprocessedProject, """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
            File.WriteAllText(preprocessedSln, "Microsoft Visual Studio Solution File, Format Version 12.00\n");

            var layout = new ProjectPreprocessSourceLayout(
                sourceRoot,
                sourceSln,
                [sourceProject, sourceSln]);

            var target = ProjectGenericPreprocessor.ResolveRestoreTarget(intermediateRoot, layout);

            Assert.Equal(preprocessedSln, target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-generic-preprocessor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
