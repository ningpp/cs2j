using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Normalization;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Fresh-conversion cleanup must keep the preprocess reports written before it
/// runs, while still deleting every other root entry.
/// </summary>
public sealed class DestinationCleanupTests
{
    [Fact]
    public void ClearDestinationForFreshConversion_KeepsPreservedReportFiles()
    {
        var destinationRoot = CreateTempDirectory("cs2j-dest-cleanup-preserved");
        var preprocessReport = Path.Combine(destinationRoot, PreprocessReportWriter.DefaultFileName);
        var residualReport = Path.Combine(destinationRoot, ResidualReportWriter.DefaultFileName);
        var staleFile = Path.Combine(destinationRoot, "keep.txt");
        var staleDirectory = Path.Combine(destinationRoot, "old");

        try
        {
            File.WriteAllText(preprocessReport, "{}");
            File.WriteAllText(residualReport, "{}");
            File.WriteAllText(staleFile, "stale");
            Directory.CreateDirectory(staleDirectory);
            File.WriteAllText(Path.Combine(staleDirectory, "Stale.java"), "class Stale {}\n");

            var preserved = new HashSet<string>(
                new[] { PreprocessReportWriter.DefaultFileName, ResidualReportWriter.DefaultFileName },
                StringComparer.OrdinalIgnoreCase);
            Program.ClearDestinationForFreshConversion(destinationRoot, preservedFileNames: preserved);

            Assert.True(File.Exists(preprocessReport));
            Assert.True(File.Exists(residualReport));
            Assert.False(File.Exists(staleFile));
            Assert.False(Directory.Exists(staleDirectory));
        }
        finally
        {
            DeleteDirectory(destinationRoot);
        }
    }

    [Fact]
    public void ClearDestinationForFreshConversion_DeletesReportsWhenNothingIsPreserved()
    {
        var destinationRoot = CreateTempDirectory("cs2j-dest-cleanup-empty");
        var preprocessReport = Path.Combine(destinationRoot, PreprocessReportWriter.DefaultFileName);
        var residualReport = Path.Combine(destinationRoot, ResidualReportWriter.DefaultFileName);

        try
        {
            File.WriteAllText(preprocessReport, "{}");
            File.WriteAllText(residualReport, "{}");

            Program.ClearDestinationForFreshConversion(
                destinationRoot,
                preservedFileNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.False(File.Exists(preprocessReport));
            Assert.False(File.Exists(residualReport));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationRoot));
        }
        finally
        {
            DeleteDirectory(destinationRoot);
        }
    }

    private static string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
