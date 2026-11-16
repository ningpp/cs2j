using System.Xml.Linq;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Re-identification of a cs2j support project previously released into the
/// source tree. Chained preprocess passes legitimately rewrite the released
/// csproj: <see cref="ProjectPreprocessSource.PinTargetFramework"/> collapses a
/// multi-target declaration to its first framework, and later passes inject
/// ProjectReferences to the other support libraries. Byte equality therefore
/// cannot recognize the copy on the next chain; compare the identity-bearing
/// Sdk attribute and PropertyGroup values (with TargetFramework(s) normalized
/// to the first entry) and ignore every ItemGroup.
/// </summary>
internal static class SupportProjectIdentity
{
    /// <summary>
    /// 各 pass 独立释放的支撑库项目文件名（保留名）。支撑库是彼此的 peer：
    /// 它们由不同 pass 分别注入，若互相写入 ProjectReference，多 pass 链就会
    /// 形成 A→B→A 的 MSB4006 restore 环（Cs2jPrelude↔Cs2jUnsignedMath）。
    /// </summary>
    private static readonly string[] SupportProjectFileNames =
    [
        Cs2jPreludeProject.ProjectFileName,
        Cs2jArrayProject.ProjectFileName,
        Cs2jRefOutInProject.ProjectFileName,
        Cs2jUnsignedMathProject.ProjectFileName,
    ];

    /// <summary>
    /// True when <paramref name="projectFilePath"/> is one of the cs2j support
    /// library projects. Injection helpers use it to keep the libraries
    /// reference-free peers (a cross reference is a restore cycle in the chain).
    /// </summary>
    public static bool IsSupportLibraryProject(string? projectFilePath)
    {
        if (string.IsNullOrEmpty(projectFilePath))
        {
            return false;
        }

        var fileName = Path.GetFileName(projectFilePath);
        return SupportProjectFileNames.Any(
            name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 每个支撑库的释放目录名 + 其 csproj 内容（身份校验用）。支撑库源码是
    /// prelude 运行时本体，任何 pass 都不得改写：改写会引入对另一个支撑库
    /// helper 的调用，从而要求注入 ProjectReference，而支撑库互相引用即是
    /// A→B→A 的 MSB4006 restore 环（Cs2jPrelude↔Cs2jUnsignedMath）。
    /// </summary>
    private static readonly (string DirectoryName, string ProjectFileName, Func<string> ReadProjectContent)[] SupportLibraries =
    [
        (PreludeSupportFiles.LibDirectoryName,
         Cs2jPreludeProject.ProjectFileName,
         PreludeSupportFiles.GetProjectFileContent),
        (ArrayHelperSupportFiles.LibDirectoryName,
         Cs2jArrayProject.ProjectFileName,
         ArrayHelperSupportFiles.GetProjectFileContent),
        (RefOutInSupportFiles.LibDirectoryName,
         Cs2jRefOutInProject.ProjectFileName,
         RefOutInSupportFiles.GetProjectFileContent),
        (UnsignedMathSupportFiles.LibDirectoryName,
         Cs2jUnsignedMathProject.ProjectFileName,
         UnsignedMathSupportFiles.GetProjectFileContent),
    ];

    /// <summary>
    /// Every cs2j support library physically present (and identity-matched) under
    /// <paramref name="sourceRoot"/>. Passes use the result as their "verbatim,
    /// never rewritten" set: the libraries are symbol sources for the C# build,
    /// not conversion input.
    /// </summary>
    public static IReadOnlyList<string> FindReleasedSupportDirectories(string sourceRoot)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var result = new List<string>();
        foreach (var (directoryName, projectFileName, readProjectContent) in SupportLibraries)
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceRoot, directoryName, options))
            {
                var projectFile = Path.Combine(directory, projectFileName);
                if (IsOwnReleaseCopy(projectFile, readProjectContent()))
                {
                    result.Add(Path.GetFullPath(directory));
                }
            }
        }

        return result;
    }

    public static bool IsOwnReleaseCopy(string projectFilePath, string expectedProjectContent)
    {
        try
        {
            if (!File.Exists(projectFilePath))
            {
                return false;
            }

            return Matches(
                File.ReadAllText(projectFilePath),
                expectedProjectContent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool Matches(string actualContent, string expectedContent)
    {
        if (!TryGetIdentitySignature(actualContent, out var actual) ||
            !TryGetIdentitySignature(expectedContent, out var expected))
        {
            return false;
        }

        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static bool TryGetIdentitySignature(string content, out string signature)
    {
        signature = string.Empty;
        XDocument document;
        try
        {
            document = XDocument.Parse(content);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }

        var root = document.Root;
        if (root == null
            || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var values = new List<string>
        {
            $"Sdk={(string?)root.Attribute("Sdk") ?? string.Empty}",
        };
        values.AddRange(root.Descendants()
            .Where(element => element.Name.LocalName == "PropertyGroup")
            .SelectMany(group => group.Elements())
            .Select(property => property.Name.LocalName is "TargetFramework" or "TargetFrameworks"
                ? $"TargetFramework={FirstFramework(property.Value)}"
                : $"{property.Name.LocalName}={property.Value.Trim()}"));
        signature = string.Join("\n", values);
        return true;
    }

    private static string FirstFramework(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
}
