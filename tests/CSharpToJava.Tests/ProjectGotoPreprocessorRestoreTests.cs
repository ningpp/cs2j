using System.Diagnostics;
using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

public class ProjectGotoPreprocessorRestoreTests
{
    [Fact]
    public async Task PreprocessAsync_RestoresProjectAndGeneratesAssetsFile()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var projectDir = Path.Combine(root, "Project");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(
                Path.Combine(projectDir, "Project.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(projectDir, "Class1.cs"),
                """
                namespace Project;

                public class Class1
                {
                    public int Value { get; set; }
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            var result = await ProjectGotoPreprocessor.PreprocessAsync(new ProjectGotoPreprocessRequest
            {
                SourcePath = Path.Combine(projectDir, "Project.csproj"),
                DestinationRoot = destinationRoot,
                Force = true,
                Verbose = false,
                Strict = false,
            });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(result.PreprocessedSourcePath), "Preprocessed project file should exist.");

            var assetsFile = Path.Combine(
                result.IntermediateRoot,
                "obj",
                "project.assets.json");
            Assert.True(
                File.Exists(assetsFile),
                $"Restore should generate project.assets.json at {assetsFile}. " +
                $"PreprocessedSourcePath={result.PreprocessedSourcePath}, IntermediateRoot={result.IntermediateRoot}");
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
        var path = Path.Combine(Path.GetTempPath(), "cs2j-goto-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
