using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Tests;

public class ConversionPipelineIgnoreDirectoryTests
{
    [Fact]
    public async Task ConvertProjectAsync_IgnoresGitDirectory()
    {
        var projectDir = CreateTempDirectory();
        try
        {
            var goodFile = Path.Combine(projectDir, "App.cs");
            File.WriteAllText(goodFile, "namespace Demo { public class App { } }");

            var gitDir = Path.Combine(projectDir, ".git");
            Directory.CreateDirectory(gitDir);
            var ignoredFile = Path.Combine(gitDir, "ShouldNotAppear.cs");
            File.WriteAllText(ignoredFile, "namespace Demo { public class ShouldNotAppear { } }");

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectAsync(projectDir, new ConversionOptions());

            Assert.Single(results);
            Assert.Equal(Path.GetFullPath(goodFile), Path.GetFullPath(results[0].FileName!));
            Assert.DoesNotContain(results, result =>
                string.Equals(
                    Path.GetFullPath(result.FileName ?? string.Empty),
                    Path.GetFullPath(ignoredFile),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertProjectWithPartialMergeAsync_IgnoresGitDirectory()
    {
        var projectDir = CreateTempDirectory();
        try
        {
            var goodFile = Path.Combine(projectDir, "Model.cs");
            File.WriteAllText(goodFile, "namespace Demo { public partial class Model { } }");

            var gitDir = Path.Combine(projectDir, ".git");
            Directory.CreateDirectory(gitDir);
            File.WriteAllText(Path.Combine(gitDir, "ShouldNotAppear.cs"), "namespace Demo { public class ShouldNotAppear { } }");

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(projectDir, new ConversionOptions());

            Assert.NotEmpty(results);
            Assert.Contains(results, result => string.Equals(result.FileName, "Model.java", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(results, result => string.Equals(result.FileName, "ShouldNotAppear.java", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-conversion-pipeline-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
