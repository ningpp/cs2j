using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

public class ProjectGotoPreprocessorTests
{
    [Fact]
    public void ResolveRestoreTarget_ForCsprojSource_ReturnsPreprocessedCsproj()
    {
        var root = CreateTempDirectory();
        try
        {
            // For a single .csproj source, ResolveSourceLayout computes SourceRoot as the
            // project directory, so the preprocessed project file lands at the root of the
            // intermediate directory.
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

            var target = ProjectGotoPreprocessor.ResolveRestoreTarget(intermediateRoot, layout);

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

            var target = ProjectGotoPreprocessor.ResolveRestoreTarget(intermediateRoot, layout);

            Assert.Equal(preprocessedSln, target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveRestoreTarget_ForDirectorySource_PrefersSlnOverCsproj()
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

            Directory.CreateDirectory(intermediateRoot);
            File.WriteAllText(
                Path.Combine(intermediateRoot, "Project.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
            File.WriteAllText(
                Path.Combine(intermediateRoot, "Solution.sln"),
                "Microsoft Visual Studio Solution File, Format Version 12.00\n");

            var layout = new ProjectPreprocessSourceLayout(
                sourceRoot,
                sourceRoot,
                [sourceProject, sourceSln]);

            var target = ProjectGotoPreprocessor.ResolveRestoreTarget(intermediateRoot, layout);

            Assert.Equal(Path.Combine(intermediateRoot, "Solution.sln"), target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Directory sources whose entry solution lives one level below the stage root
    /// (&lt;stage&gt;\json\System.Text.Json.sln) must still be restorable: the restore
    /// target feeds MSBuildWorkspace, and without it every project loads with an
    /// empty metadata reference set.
    /// </summary>
    [Fact]
    public void ResolveRestoreTarget_ForDirectorySourceWithNestedSln_ReturnsNestedSln()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            var intermediateRoot = Path.Combine(root, "intermediate");
            Directory.CreateDirectory(Path.Combine(sourceRoot, "json", "src"));
            File.WriteAllText(
                Path.Combine(sourceRoot, "json", "System.Text.Json.sln"),
                "Microsoft Visual Studio Solution File, Format Version 12.00\n");

            var preprocessedSln = Path.Combine(intermediateRoot, "json", "System.Text.Json.sln");
            Directory.CreateDirectory(Path.GetDirectoryName(preprocessedSln)!);
            File.WriteAllText(preprocessedSln, "Microsoft Visual Studio Solution File, Format Version 12.00\n");

            var layout = new ProjectPreprocessSourceLayout(
                sourceRoot,
                sourceRoot,
                [Path.Combine(sourceRoot, "json", "System.Text.Json.sln")]);

            var target = ProjectGotoPreprocessor.ResolveRestoreTarget(intermediateRoot, layout);

            Assert.Equal(preprocessedSln, target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A solution entry is copied to the intermediate tree with its original
    /// repository-relative path, so restore must target that exact location rather
    /// than a flattened name at the stage root.
    /// </summary>
    [Fact]
    public void ResolveRestoreTarget_ForNestedSlnSource_ReturnsItsCopiedLocation()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            var intermediateRoot = Path.Combine(root, "intermediate");
            var sourceSln = Path.Combine(sourceRoot, "json", "System.Text.Json.sln");
            var sourceProject = Path.Combine(sourceRoot, "json", "src", "System.Text.Json.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceProject)!);
            File.WriteAllText(sourceProject, """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
            File.WriteAllText(sourceSln, "Microsoft Visual Studio Solution File, Format Version 12.00\n");

            var preprocessedSln = Path.Combine(intermediateRoot, "json", "System.Text.Json.sln");
            var preprocessedProject = Path.Combine(intermediateRoot, "json", "src", "System.Text.Json.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(preprocessedProject)!);
            File.WriteAllText(preprocessedProject, """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
            File.WriteAllText(preprocessedSln, "Microsoft Visual Studio Solution File, Format Version 12.00\n");

            var layout = new ProjectPreprocessSourceLayout(
                sourceRoot,
                sourceSln,
                [sourceProject, sourceSln]);

            var target = ProjectGotoPreprocessor.ResolveRestoreTarget(intermediateRoot, layout);

            Assert.Equal(preprocessedSln, target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-goto-preprocessor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
