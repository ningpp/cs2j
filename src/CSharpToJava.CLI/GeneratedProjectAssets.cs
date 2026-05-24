using CSharpToJava.Core.Pipeline.Planning;

namespace CSharpToJava.CLI;

internal static class GeneratedProjectAssets
{
    private const string GitIgnoreFileName = ".gitignore";

    public static void WriteRootFiles(string destinationRoot, OutputIncrementalWriteSession outputSession)
    {
        WriteGitIgnoreFile(destinationRoot, outputSession, ResolveRequiredGitIgnoreTemplatePath());
        WriteMavenConfigFile(destinationRoot, outputSession);
    }

    public static IReadOnlyList<string> GetTemplateAssetPaths()
    {
        return [ResolveRequiredGitIgnoreTemplatePath()];
    }

    internal static void WriteGitIgnoreFile(string destinationRoot, OutputIncrementalWriteSession outputSession, string templatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(outputSession);
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);

        var fullTemplatePath = Path.GetFullPath(templatePath);
        if (!File.Exists(fullTemplatePath))
        {
            throw new FileNotFoundException($"Git ignore template not found: {fullTemplatePath}", fullTemplatePath);
        }

        var outputPath = Path.Combine(destinationRoot, GitIgnoreFileName);
        var content = File.ReadAllText(fullTemplatePath);
        outputSession.WriteTextFile(outputPath, content, OutputIncrementalEntryKind.BuildFile);
    }

    internal static void WriteMavenConfigFile(string destinationRoot, OutputIncrementalWriteSession outputSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(outputSession);

        var generator = new MavenPomGenerator();
        var outputPath = Path.Combine(destinationRoot, ".mvn", "maven.config");
        outputSession.WriteTextFile(outputPath, generator.GenerateMavenConfig(), OutputIncrementalEntryKind.BuildFile);
    }

    internal static string ResolveRequiredGitIgnoreTemplatePath(string? currentDirectory = null, string? appBaseDirectory = null)
    {
        var resolvedPath = TryResolveGitIgnoreTemplatePath(currentDirectory, appBaseDirectory);
        if (!string.IsNullOrEmpty(resolvedPath))
        {
            return resolvedPath;
        }

        throw new FileNotFoundException(
            "Generated project .gitignore template not found. Expected config\\.gitignore in the CLI output, current workspace, or one of their parent directories.");
    }

    internal static string? TryResolveGitIgnoreTemplatePath(string? currentDirectory = null, string? appBaseDirectory = null)
    {
        foreach (var root in EnumerateCandidateRoots(currentDirectory, appBaseDirectory))
        {
            var candidatePath = Path.Combine(root, "config", GitIgnoreFileName);
            if (File.Exists(candidatePath))
            {
                return Path.GetFullPath(candidatePath);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateRoots(string? currentDirectory, string? appBaseDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var startDirectories = new[]
        {
            appBaseDirectory,
            currentDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
        };

        foreach (var startDirectory in startDirectories)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                continue;
            }

            var fullStartDirectory = Path.GetFullPath(startDirectory);
            for (var directory = new DirectoryInfo(fullStartDirectory); directory != null; directory = directory.Parent)
            {
                if (seen.Add(directory.FullName))
                {
                    yield return directory.FullName;
                }
            }
        }
    }
}
