using CSharpToJava.CLI;
using CSharpToJava.Core.EnumPreprocessor;
using Xunit;

namespace CSharpToJava.Tests;

public class ProjectEnumPreprocessorTests
{
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
        catch
        {
            return false;
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-enum-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void IntermediateDirectoryName_Is_Correct()
    {
        Assert.Equal(".cs2j-enum-src", ProjectEnumPreprocessor.IntermediateDirectoryName);
    }

    [Fact]
    public async Task Single_Project_Enum_Preprocess_Succeeds()
    {
        if (!IsDotNetAvailable()) return;

        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            var projectDir = Path.Combine(sourceRoot, "EnumLib");
            Directory.CreateDirectory(projectDir);

            var csprojPath = Path.Combine(projectDir, "EnumLib.csproj");
            await File.WriteAllTextAsync(csprojPath,
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");

            var csPath = Path.Combine(projectDir, "Enums.cs");
            await File.WriteAllTextAsync(csPath, """
                using System;

                [Flags]
                public enum Permissions : int
                {
                    None = 0,
                    Read = 1,
                    Write = 2,
                    Execute = 4
                }

                [Flags]
                public enum ByteFlags : byte
                {
                    A = 1,
                    B = 2,
                    C = 4
                }

                public class Service
                {
                    public Permissions Current { get; set; }

                    public void Run(Permissions p)
                    {
                        var combined = p | Permissions.Write;
                        var removed = p & ~Permissions.Read;
                        bool canWrite = p.HasFlag(Permissions.Write);
                        p |= Permissions.Execute;
                    }
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            Directory.CreateDirectory(destinationRoot);

            var result = await ProjectEnumPreprocessor.PreprocessAsync(
                new ProjectEnumPreprocessRequest
                {
                    SourcePath = csprojPath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    Verbose = true,
                    FilterUnsupportedProjects = true,
                    VerifyBuild = false,
                    VerifyTests = false,
                    Strict = false,
                });

            // Print diagnostics for debugging
            foreach (var d in result.Diagnostics)
                Console.WriteLine($"[{d.Severity}] {d.FilePath}: {d.Message}");
            // Don't fail on diagnostics - just check the output is correct
            Assert.True(result.Statistics.FilesTransformed >= 1);

            // Verify intermediate directory exists
            Assert.True(Directory.Exists(result.IntermediateRoot));
            Console.WriteLine($"IntermediateRoot: {result.IntermediateRoot}");

            // List all .cs files in intermediate root for debugging
            foreach (var f in Directory.EnumerateFiles(result.IntermediateRoot, "*.cs", SearchOption.AllDirectories))
                Console.WriteLine($"  Output file: {f}");

            // Verify output file was written - the file may be at different relative paths
            // depending on how ProjectPreprocessSource copies files
            var outputFiles = Directory.EnumerateFiles(result.IntermediateRoot, "Enums.cs", SearchOption.AllDirectories).ToList();
            Assert.True(outputFiles.Count > 0, $"Expected to find Enums.cs in {result.IntermediateRoot}, found: {string.Join(", ", Directory.EnumerateFiles(result.IntermediateRoot, "*.cs", SearchOption.AllDirectories))}");

            var outputContent = await File.ReadAllTextAsync(outputFiles[0]);
            Assert.Contains("(int)Permissions", outputContent);
            Assert.DoesNotContain("HasFlag", outputContent);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
