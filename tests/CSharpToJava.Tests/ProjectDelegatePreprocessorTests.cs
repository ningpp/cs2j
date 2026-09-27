using CSharpToJava.CLI;
using Xunit;

namespace CSharpToJava.Tests;

public class ProjectDelegatePreprocessorTests
{
    [Fact]
    public async Task PreprocessAsync_SingleProject_Builds_Successfully()
    {
        if (!IsDotNetAvailable())
            return;

        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceRoot);

            var projectPath = Path.Combine(sourceRoot, "Lib.csproj");
            await File.WriteAllTextAsync(projectPath,
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");

            var csPath = Path.Combine(sourceRoot, "Lib.cs");
            await File.WriteAllTextAsync(csPath, """
                using System;
                class Lib {
                    void M(Action d1, Action d2, Action h) {
                        Action d = d1 + d2;
                        d += h;
                        d -= h;
                    }
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            Directory.CreateDirectory(destinationRoot);

            var result = await ProjectDelegatePreprocessor.PreprocessAsync(
                new ProjectDelegatePreprocessRequest
                {
                    SourcePath = projectPath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = false,
                    VerifyBuild = true,
                    VerifyTests = false,
                    Strict = false,
                });

            Assert.True(result.Success,
                string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            Assert.True(result.Statistics.FilesTransformed > 0);

            // Verify Cs2jPrelude support files were released
            var preludeDir = Path.Combine(
                result.IntermediateRoot,
                PreludeSupportFiles.LibDirectoryName);
            Assert.True(File.Exists(Path.Combine(preludeDir, "CsPrelude.cs")));
            Assert.True(File.Exists(Path.Combine(preludeDir, "Cs2jPrelude.csproj")));

            // Verify output contains Combine / Remove calls
            var outputCs = Path.Combine(result.IntermediateRoot, "Lib.cs");
            var output = await File.ReadAllTextAsync(outputCs);
            Assert.Contains("CsPrelude.Combine", output);
            Assert.Contains("CsPrelude.Remove", output);
            Assert.DoesNotContain("d1 + d2", output);
            Assert.DoesNotContain("d += h", output);
            Assert.DoesNotContain("d -= h", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PreprocessAsync_CleanFile_ByteLevelPreserved()
    {
        if (!IsDotNetAvailable())
            return;

        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceRoot);

            var projectPath = Path.Combine(sourceRoot, "Lib.csproj");
            await File.WriteAllTextAsync(projectPath,
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");

            var csPath = Path.Combine(sourceRoot, "Lib.cs");
            var content = """
                class Lib {
                    int Add(int a, int b) => a + b;
                }
                """;
            await File.WriteAllTextAsync(csPath, content);

            var destinationRoot = Path.Combine(root, "out");
            Directory.CreateDirectory(destinationRoot);

            var result = await ProjectDelegatePreprocessor.PreprocessAsync(
                new ProjectDelegatePreprocessRequest
                {
                    SourcePath = projectPath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = false,
                    VerifyBuild = false,
                    VerifyTests = false,
                });

            Assert.True(result.Success);
            Assert.True(result.Statistics.FilesSkippedClean > 0);

            var outputCs = Path.Combine(result.IntermediateRoot, "Lib.cs");
            var output = await File.ReadAllTextAsync(outputCs);
            Assert.Equal(content, output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static bool IsDotNetAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-delegate-test-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }
}
