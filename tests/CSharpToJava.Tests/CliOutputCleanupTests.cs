using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

public class CliOutputCleanupTests
{
    [Fact]
    public void ClearDestinationForFreshConversion_RemovesChildrenButKeepsRootDirectory()
    {
        var destinationRoot = Path.Combine(Path.GetTempPath(), "cs2j-cli-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destinationRoot);

        try
        {
            var nestedDirectory = Path.Combine(destinationRoot, "module", "src");
            Directory.CreateDirectory(nestedDirectory);
            var staleFile = Path.Combine(nestedDirectory, "Stale.java");
            File.WriteAllText(staleFile, "class Stale {}\n");

            Program.ClearDestinationForFreshConversion(destinationRoot);

            Assert.True(Directory.Exists(destinationRoot));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationRoot));
        }
        finally
        {
            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, recursive: true);
            }
        }
    }
}
