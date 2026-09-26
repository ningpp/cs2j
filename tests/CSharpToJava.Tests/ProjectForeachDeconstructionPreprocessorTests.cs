using CSharpToJava.CLI;
using System.Diagnostics;

namespace CSharpToJava.Tests;

public class ProjectForeachDeconstructionPreprocessorTests
{
    [Fact]
    public async Task PreprocessAsync_BasicKv_BuildsSuccessfully()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var projectDir = Path.Combine(root, "TestProj");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(
                Path.Combine(projectDir, "TestProj.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                    <LangVersion>latest</LangVersion>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(projectDir, "Program.cs"),
                """
                using System;
                using System.Collections.Generic;

                class Program
                {
                    static void Main(string[] args)
                    {
                        var dict = new Dictionary<int, string>
                        {
                            { 1, "one" },
                            { 2, "two" }
                        };

                        foreach ((int k, string v) in dict)
                        {
                            Console.WriteLine($"{k}={v}");
                        }
                    }
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            var result = await ProjectForeachDeconstructionPreprocessor.PreprocessAsync(
                new ProjectForeachDeconstructionPreprocessRequest
                {
                    SourcePath = Path.Combine(projectDir, "TestProj.csproj"),
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = false,
                });

            Assert.True(result.Success,
                "Preprocess should succeed. Diagnostics:\n"
                + string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));
            Assert.True(result.Statistics.ForeachDeconstructionsLowered > 0,
                "Expected at least one deconstruction foreach to be lowered.");

            // Verify the output contains the lowered form.
            var outputFile = Path.Combine(result.IntermediateRoot, "Program.cs");
            var outputContent = await File.ReadAllTextAsync(outputFile);
            Assert.Contains("__entry.Key", outputContent, StringComparison.Ordinal);
            Assert.Contains("__entry.Value", outputContent, StringComparison.Ordinal);

            // Verify the intermediate project builds.
            var (exitCode, stdOut, stdErr) = RunDotNet(
                $"build -c Release",
                result.IntermediateRoot);
            Assert.True(exitCode == 0,
                $"dotnet build failed (exit {exitCode}):\n{stdErr}\n{stdOut}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PreprocessAsync_CleanFile_ByteIdentical()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var projectDir = Path.Combine(root, "TestProj");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(
                Path.Combine(projectDir, "TestProj.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                </Project>
                """);
            var sourceContent = """
                using System;
                using System.Collections.Generic;

                class Program
                {
                    static void Main()
                    {
                        var list = new List<int> { 1, 2, 3 };
                        foreach (var item in list)
                        {
                            Console.WriteLine(item);
                        }
                    }
                }
                """;
            File.WriteAllText(Path.Combine(projectDir, "Program.cs"), sourceContent);

            var destinationRoot = Path.Combine(root, "out");
            var result = await ProjectForeachDeconstructionPreprocessor.PreprocessAsync(
                new ProjectForeachDeconstructionPreprocessRequest
                {
                    SourcePath = Path.Combine(projectDir, "TestProj.csproj"),
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = false,
                });

            Assert.True(result.Success);
            Assert.Equal(0, result.Statistics.ForeachDeconstructionsLowered);

            var outputFile = Path.Combine(result.IntermediateRoot, "Program.cs");
            var outputContent = await File.ReadAllTextAsync(outputFile);
            Assert.Equal(sourceContent, outputContent);
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
        var path = Path.Combine(Path.GetTempPath(), "cs2j-foreach-decon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunDotNet(string arguments, string workingDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo);
        if (process == null) return (-1, "", "Process.Start returned null");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdOut, stdErr);
    }
}
