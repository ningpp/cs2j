using System.Diagnostics;
using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

public class ProjectPreprocessorsConsistentTests
{
    private static readonly IReadOnlyList<string> WindowsCompatNamespacePrefixes =
    [
        "System.Windows.Forms",
        "System.Windows",
    ];

    [Fact]
    public async Task AllPreprocessors_UseSameProjectSelection_AndExcludeWindowsProjects()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceRoot);

            var libProject = CreateProject(sourceRoot, "Lib", isTest: false);
            var wpfProject = CreateProject(
                sourceRoot,
                "WpfApp",
                isTest: false,
                sdk: "Microsoft.NET.Sdk.WindowsDesktop",
                targetFramework: "net8.0-windows",
                useWpf: true);
            // A Windows/WPF project that is NOT part of the solution must also be
            // excluded from every intermediate tree by the same directory scan.
            CreateProject(
                sourceRoot,
                "OrphanWpf",
                isTest: false,
                sdk: "Microsoft.NET.Sdk.WindowsDesktop",
                targetFramework: "net8.0-windows",
                useWpf: true);
            var solutionPath = CreateSolution(sourceRoot, "Solution.sln", [libProject, wpfProject]);

            var destinationRoot = Path.Combine(root, "out");

            var unsigned = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = solutionPath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = true,
                    CompatNamespacePrefixes = WindowsCompatNamespacePrefixes,
                });
            Assert.True(unsigned.Success, string.Join("\n", unsigned.Diagnostics.Select(d => d.Message)));

            var readonlyResult = await ProjectReadOnlyStructPreprocessor.PreprocessAsync(
                new ProjectReadOnlyPreprocessRequest
                {
                    SourcePath = unsigned.PreprocessedSourcePath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = true,
                    CompatNamespacePrefixes = WindowsCompatNamespacePrefixes,
                    VerifyBuild = false,
                    VerifyTests = false,
                });
            Assert.True(
                readonlyResult.Success,
                string.Join("\n", readonlyResult.Diagnostics.Select(d => $"[{d.Severity}] {d.Reason}")));
            var gotoResult = await ProjectGotoPreprocessor.PreprocessAsync(
                new ProjectGotoPreprocessRequest
                {
                    SourcePath = readonlyResult.PreprocessedSourcePath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = true,
                    CompatNamespacePrefixes = WindowsCompatNamespacePrefixes,
                });
            Assert.True(gotoResult.Success, string.Join("\n", gotoResult.Diagnostics.Select(d => d.Message)));

            var intermediateRoots = new[]
            {
                unsigned.IntermediateRoot,
                readonlyResult.IntermediateRoot,
                gotoResult.IntermediateRoot,
            };

            var projectPathSets = intermediateRoots
                .Select(CollectProjectFilePaths)
                .ToArray();

            foreach (var paths in projectPathSets)
            {
                Assert.DoesNotContain(paths, p => p.Contains("wpfapp", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(paths, p => p.Contains("orphanwpf", StringComparison.OrdinalIgnoreCase));
            }

            Assert.Equal(projectPathSets[0], projectPathSets[1]);
            Assert.Equal(projectPathSets[0], projectPathSets[2]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<string> CollectProjectFilePaths(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .Where(path => !HasArtifactSegment(path))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/').ToLowerInvariant())
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasArtifactSegment(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateProject(
        string root,
        string name,
        bool isTest,
        string sdk = "Microsoft.NET.Sdk",
        string targetFramework = "net10.0",
        bool useWpf = false,
        bool useWindowsForms = false)
    {
        var projectDir = Path.Combine(root, name);
        Directory.CreateDirectory(projectDir);

        var useWpfProperty = useWpf ? "    <UseWPF>true</UseWPF>" + Environment.NewLine : string.Empty;
        var useWindowsFormsProperty = useWindowsForms ? "    <UseWindowsForms>true</UseWindowsForms>" + Environment.NewLine : string.Empty;
        var isTestProperty = isTest ? "    <IsTestProject>true</IsTestProject>" + Environment.NewLine : string.Empty;

        var projectPath = Path.Combine(projectDir, name + ".csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"" + sdk + "\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>" + targetFramework + "</TargetFramework>\n" +
            isTestProperty + useWpfProperty + useWindowsFormsProperty +
            "  </PropertyGroup>\n" +
            "</Project>\n");
        File.WriteAllText(
            Path.Combine(projectDir, "Class1.cs"),
            "namespace " + name + ";\n\n" +
            "public class Class1\n" +
            "{\n" +
            "    public int Value { get; set; }\n" +
            "}\n");
        return projectPath;
    }

    private static string CreateSolution(string root, string fileName, IReadOnlyList<string> projectPaths)
    {
        var lines = new List<string>
        {
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17",
        };

        foreach (var projectPath in projectPaths)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var relativePath = Path.GetRelativePath(root, projectPath).Replace(Path.DirectorySeparatorChar, '\\');
            lines.Add($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projectName}\", \"{relativePath}\", \"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}\"");
            lines.Add("EndProject");
        }

        var solutionPath = Path.Combine(root, fileName);
        File.WriteAllLines(solutionPath, lines);
        return solutionPath;
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
        var path = Path.Combine(Path.GetTempPath(), "cs2j-preprocess-consistent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
