using System.Text;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Workspace;

/// <summary>
/// Expands MSBuild-style glob patterns (single * and recursive **) used in
/// &lt;None&gt;/&lt;Content&gt; Include attributes into concrete file paths relative
/// to the project directory.
/// </summary>
public static class ProjectGlobExpander
{
    /// <summary>
    /// Enumerates all files under <paramref name="projectDir"/> whose path
    /// relative to the project directory matches the given glob pattern.
    /// </summary>
    public static IEnumerable<string> Expand(string projectDir, string include)
    {
        var normalized = include.Replace('\\', '/');
        var starIndex = normalized.IndexOf('*');
        if (starIndex < 0)
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectDir, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(fullPath))
            {
                yield return fullPath;
            }

            yield break;
        }

        // Only walk the static prefix directory to keep the scan bounded.
        var staticPrefix = normalized.Substring(0, starIndex).TrimEnd('/');
        var searchDir = string.IsNullOrEmpty(staticPrefix)
            ? projectDir
            : Path.GetFullPath(Path.Combine(projectDir, staticPrefix.Replace('/', Path.DirectorySeparatorChar)));

        if (!Directory.Exists(searchDir))
        {
            yield break;
        }

        var regex = BuildGlobRegex(normalized);
        foreach (var file in Directory.EnumerateFiles(searchDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
            if (regex.IsMatch(relativePath))
            {
                yield return file;
            }
        }
    }

    private static Regex BuildGlobRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    // '**' absorbs any number of path segments (including none).
                    sb.Append(".*");
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        sb.Append("/?");
                        i++;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        var options = OperatingSystem.IsWindows()
            ? RegexOptions.Compiled | RegexOptions.IgnoreCase
            : RegexOptions.Compiled;
        return new Regex(sb.ToString(), options);
    }
}
