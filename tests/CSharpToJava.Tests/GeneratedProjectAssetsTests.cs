using CSharpToJava.CLI;
using CSharpToJava.Core.Pipeline.Planning;

namespace CSharpToJava.Tests;

public class GeneratedProjectAssetsTests
{
    [Fact]
    public void ResolveRequiredGitIgnoreTemplatePath_FindsTemplateInParentConfigDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-generated-assets-resolve-" + Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(tempRoot, "repo");
        var configDir = Path.Combine(repoRoot, "config");
        var appBaseDirectory = Path.Combine(repoRoot, "src", "CSharpToJava.CLI", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(appBaseDirectory);

        try
        {
            var templatePath = Path.Combine(configDir, ".gitignore");
            File.WriteAllText(templatePath, "target/\n");

            var resolved = GeneratedProjectAssets.ResolveRequiredGitIgnoreTemplatePath(
                currentDirectory: Path.Combine(tempRoot, "scratch"),
                appBaseDirectory: appBaseDirectory);

            Assert.Equal(Path.GetFullPath(templatePath), resolved);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteGitIgnoreFile_CopiesTemplateToDestinationRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-generated-assets-write-" + Guid.NewGuid().ToString("N"));
        var templateDir = Path.Combine(tempRoot, "config");
        var destinationRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(templateDir);

        try
        {
            var templatePath = Path.Combine(templateDir, ".gitignore");
            await File.WriteAllTextAsync(templatePath, "target/\n*.log\n");

            var session = OutputIncrementalWriteSession.Create(destinationRoot, tempRoot);
            GeneratedProjectAssets.WriteGitIgnoreFile(destinationRoot, session, templatePath);
            await session.SaveAsync();

            var generatedPath = Path.Combine(destinationRoot, ".gitignore");
            Assert.True(File.Exists(generatedPath));
            Assert.Equal("target/\n*.log\n", await File.ReadAllTextAsync(generatedPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}