namespace CSharpToJava.ProjectTests;

/// <summary>
/// Locates the CSharpToJava.CLI build output for end-to-end CLI tests.
/// The walk-up search prefers the same MSBuild configuration (Debug/Release)
/// as the running test assembly and falls back to the other configuration, so
/// the tests pass under both <c>dotnet test</c> and <c>dotnet test -c Release</c>.
/// </summary>
internal static class TestCliPaths
{
    public static string LocateCli()
    {
        var dir = AppContext.BaseDirectory;
        var configurations = DetectConfiguration(dir) switch
        {
            "Release" => new[] { "Release", "Debug" },
            "Debug" => new[] { "Debug", "Release" },
            _ => new[] { "Release", "Debug" },
        };

        while (dir is not null)
        {
            foreach (var configuration in configurations)
            {
                var candidate = Path.Combine(
                    dir, "src", "CSharpToJava.CLI", "bin", configuration, "net10.0",
                    "CSharpToJava.CLI.dll");
                if (File.Exists(candidate)) return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(
            "Could not locate CSharpToJava.CLI.dll from test output directory.");
    }

    /// <summary>
    /// Extracts the MSBuild configuration segment from an output path shaped
    /// like <c>...\bin\Release\net10.0\</c>.
    /// </summary>
    private static string? DetectConfiguration(string baseDirectory)
    {
        var parts = baseDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (string.Equals(parts[i], "bin", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return null;
    }
}
