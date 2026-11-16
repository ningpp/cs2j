using System.Xml.Linq;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Shared "wire a released support project into a host project" step: point a
/// <c>.csproj</c> at the injected support library exactly once, and — when the
/// library physically lives inside the host project directory — keep its sources
/// out of the host's default <c>Compile</c> glob.
/// <para>
/// Several passes historically carried private copies of this block
/// (delegate / equality-concat / ref-out-in / unsigned-math / member-desugar).
/// New passes call this helper instead of adding another copy; the existing
/// copies are intentionally left as they are so their byte-for-byte output does
/// not change.
/// </para>
/// </summary>
internal static class SupportLibraryProjectReference
{
    /// <summary>
    /// Ensure <paramref name="projectFilePath"/> has a <c>ProjectReference</c> to
    /// <paramref name="libProjectPath"/>. Idempotent: an existing reference to the
    /// same library is replaced, never duplicated.
    /// </summary>
    public static void Ensure(
        string projectFilePath,
        string libProjectPath,
        string libDirectoryName,
        string intermediateRoot,
        Action<string> reportWarning)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        // The released support project is the reference target itself; adding the
        // reference would create a self-reference (and an MSB4006 restore cycle)
        // whenever the input tree already ships the support project.
        if (PathsEqual(projectFilePath, libProjectPath))
        {
            return;
        }

        // 支撑库是彼此的 peer：交叉注入会让多 pass 链形成 A→B→A 的 restore 环。
        if (SupportProjectIdentity.IsSupportLibraryProject(projectFilePath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            reportWarning($"could not read '{projectFilePath}' to add the {libDirectoryName} reference: {ex.Message}");
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            reportWarning($"'{projectFilePath}' is not an MSBuild project; the {libDirectoryName} reference was skipped.");
            return;
        }

        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;
        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDirectory, intermediateRoot))
        {
            var removePattern = Path.Combine(libDirectoryName, "**");
            if (!root.Descendants().Any(element =>
                    element.Name.LocalName == "Compile"
                    && string.Equals((string?)element.Attribute("Remove"), removePattern, StringComparison.OrdinalIgnoreCase)))
            {
                root.Add(
                    new XText(Environment.NewLine + "  "),
                    new XElement("ItemGroup",
                        new XElement("Compile", new XAttribute("Remove", removePattern))),
                    new XText(Environment.NewLine));
            }
        }

        foreach (var referenceElement in root.Descendants()
                     .Where(element => element.Name.LocalName == "ProjectReference")
                     .ToList())
        {
            var include = (string?)referenceElement.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include!));
            if (PathsEqual(resolvedPath, libProjectPath))
            {
                referenceElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, libProjectPath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", relativeInclude))),
            new XText(Environment.NewLine));

        try
        {
            document.Save(projectFilePath, SaveOptions.DisableFormatting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reportWarning($"could not write '{projectFilePath}': {ex.Message}");
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string? GetPropertyValue(XElement root, string name)
    {
        var propertyGroup = root.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "PropertyGroup"
                && element.Elements().Any(property => property.Name.LocalName == name));
        return propertyGroup?.Element(name)?.Value;
    }
}
