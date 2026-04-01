namespace CSharpToJava.Tests;

/// <summary>
/// Shared helpers for tests that need to locate repository-level fixture directories.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Returns the path to the <c>config/java/</c> directory by walking up from the test
    /// assembly's base directory until a directory containing <c>java.base</c> is found.
    /// </summary>
    public static string JavaConfigDir { get; } = FindJavaConfigDir();

    private static string FindJavaConfigDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "config", "java");
            if (Directory.Exists(Path.Combine(candidate, "java.base")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(AppContext.BaseDirectory, "config", "java");
    }
}
