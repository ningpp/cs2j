using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

public sealed class UnifiedPreprocessedSrcTests
{
    private const string UnifiedDirectoryName = ".cs2j-preprocessed-src";

    [SlowE2EFact(7.0)]
    public async Task Preprocess_UnifiedFlag_AllPassesShareOneDirectoryAndRewriteInPlace()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "source");
        var destinationRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "Sample.cs"),
                "class Sample { void M() { goto Done; Done: return; } }");
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "data.txt"), "payload");

            var exit = await Program.MainImpl([
                "preprocess",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--unified-preprocessed-src",
            ]);

            Assert.Equal(0, exit);

            var intermediateDirs = Directory
                .GetDirectories(destinationRoot, ".cs2j-*-src")
                .Select(Path.GetFileName)
                .ToList();
            Assert.Single(intermediateDirs);
            Assert.Equal(UnifiedDirectoryName, intermediateDirs[0]);

            // The first pass copied non-source resources in; later passes kept them.
            Assert.Equal("payload", await File.ReadAllTextAsync(
                Path.Combine(destinationRoot, UnifiedDirectoryName, "data.txt")));

            // The default chain includes eliminate-goto: the shared tree holds
            // the cumulative, rewritten output of every pass.
            var unifiedSource = await File.ReadAllTextAsync(
                Path.Combine(destinationRoot, UnifiedDirectoryName, "Sample.cs"));
            Assert.DoesNotContain("goto", unifiedSource, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [SlowE2EFact(26.9)]
    public async Task ConvertProject_UnifiedFlag_UsesSingleIntermediateDirectory()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "Demo");
        var destinationRoot = Path.Combine(tempRoot, "java");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "Sample.cs"),
                "class Sample { void M() { goto Done; Done: return; } }");

            var exit = await Program.MainImpl([
                "convert-project",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--no-cache",
                "--unified-preprocessed-src",
            ]);

            Assert.Equal(0, exit);
            Assert.True(Directory.Exists(Path.Combine(destinationRoot, UnifiedDirectoryName)));

            var intermediateDirs = Directory
                .GetDirectories(destinationRoot, ".cs2j-*-src")
                .Select(Path.GetFileName)
                .ToList();
            Assert.Single(intermediateDirs);
            Assert.Equal(UnifiedDirectoryName, intermediateDirs[0]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [SlowE2EFact(10.1)]
    public async Task ConvertProject_WithoutUnifiedFlag_KeepsPerPassDirectories()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "Demo");
        var destinationRoot = Path.Combine(tempRoot, "java");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, "Sample.cs"),
                "class Sample { void M() { goto Done; Done: return; } }");

            var exit = await Program.MainImpl([
                "convert-project",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--no-cache",
            ]);

            Assert.Equal(0, exit);

            // Default behavior is unchanged: every pass owns its own tree copy.
            Assert.False(Directory.Exists(Path.Combine(destinationRoot, UnifiedDirectoryName)));
            Assert.True(Directory.Exists(
                Path.Combine(destinationRoot, ProjectGotoPreprocessor.IntermediateDirectoryName)));
            Assert.True(Directory
                .GetDirectories(destinationRoot, ".cs2j-*-src")
                .Select(Path.GetFileName)
                .Count() > 1);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-unified-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
