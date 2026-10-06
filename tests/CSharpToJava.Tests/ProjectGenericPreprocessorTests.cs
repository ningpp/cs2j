using System.Diagnostics;
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

    [Fact]
    public async Task PreprocessAsync_RewritesDefaultAndNewT_VerifiesBuildPasses()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var projectDir = Path.Combine(root, "GenericLib");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(
                Path.Combine(projectDir, "GenericLib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(projectDir, "Container.cs"),
                """
                namespace GenericLib;

                public class Container<T> where T : new()
                {
                    public T Create() => new T();

                    public T Default() => default(T);

                    public T[] MakeArray(int n) => new T[n];
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            var result = await ProjectGenericPreprocessor.PreprocessAsync(new ProjectGenericPreprocessRequest
            {
                SourcePath = Path.Combine(projectDir, "GenericLib.csproj"),
                DestinationRoot = destinationRoot,
                Force = true,
                FilterUnsupportedProjects = false,
                VerifyBuild = true,
                VerifyTests = false,
            });

            Assert.True(
                result.Success,
                "Verification build should pass for generic-preprocessed C#. Diagnostics:\n"
                + string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));
            Assert.True(result.Statistics.DefaultExpressionsRewritten >= 1, "default(T) should be rewritten");
            Assert.True(result.Statistics.NewTypeParamRewritten >= 1, "new T() should be rewritten");
            Assert.True(result.Statistics.NewTypeParamArrayRewritten >= 1, "new T[n] should be rewritten");

            // The intermediate directory must exist and contain the rewritten file.
            Assert.True(Directory.Exists(result.IntermediateRoot));
            var rewrittenFile = Path.Combine(result.IntermediateRoot, "Container.cs");
            Assert.True(File.Exists(rewrittenFile));
            var rewritten = await File.ReadAllTextAsync(rewrittenFile);
            Assert.DoesNotContain("default(T)", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("new T()", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("new T[", rewritten, StringComparison.Ordinal);
            Assert.Contains("Activator.CreateInstance<T>", rewritten, StringComparison.Ordinal);
            Assert.Contains("Array.CreateInstance", rewritten, StringComparison.Ordinal);

            Assert.Empty(
                Directory.EnumerateDirectories(result.IntermediateRoot, "bin", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SlowE2EFact(28.31)]
    public async Task PreprocessAsync_VerifyTests_PassesForGenericTestProject()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var projectDir = Path.Combine(root, "GenericTests");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(
                Path.Combine(projectDir, "GenericTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(projectDir, "ContainerTests.cs"),
                """
                using Xunit;

                namespace GenericTests;

                public class Container<T> where T : new()
                {
                    public T Create() => new T();
                    public T Default() => default(T);
                }

                public class ContainerTests
                {
                    [Fact]
                    public void Create_ReturnsDefaultInt()
                    {
                        var c = new Container<int>();
                        Assert.Equal(0, c.Create());
                        Assert.Equal(0, c.Default());
                    }
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            var result = await ProjectGenericPreprocessor.PreprocessAsync(new ProjectGenericPreprocessRequest
            {
                SourcePath = Path.Combine(projectDir, "GenericTests.csproj"),
                DestinationRoot = destinationRoot,
                Force = true,
                FilterUnsupportedProjects = false,
                VerifyBuild = true,
                VerifyTests = true,
            });

            Assert.True(
                result.Success,
                "Verification build+test should pass for generic-preprocessed C# test project. Diagnostics:\n"
                + string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsDotNetAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process == null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-generic-preprocessor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
