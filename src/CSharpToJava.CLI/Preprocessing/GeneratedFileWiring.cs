using System.Xml.Linq;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Shared wiring for pass-generated helper files (generic-deepen holders, closed-metadata
/// registration files, ...): source→intermediate path mapping and the idempotent csproj
/// Compile Include/Link hookup. Extracted from GenericDeepenPassAdapter so every
/// file-generating pass shares one ownership/wiring algorithm (design D6).
/// </summary>
internal static class GeneratedFileWiring
{
    public static string ToIntermediatePath(string filePath, string sourceRoot, string intermediateRoot)
        => Path.GetFullPath(Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, filePath)));

    public static void EnsureCompileItem(
        string projectFilePath,
        string fileName,
        string projectDirectory,
        string sharedFilePath)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var normalizedFileName = fileName.Replace('\\', '/');
        foreach (var compileElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "Compile").ToList())
        {
            var include = (string?)compileElement.Attribute("Include");
            var link = (string?)compileElement.Attribute("Link");
            if (MatchesHelperFile(include, normalizedFileName)
                || MatchesHelperFile(link, normalizedFileName))
            {
                compileElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, sharedFilePath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement(
                "ItemGroup",
                new XElement(
                    "Compile",
                    new XAttribute("Include", relativeInclude),
                    new XAttribute("Link", fileName))),
            new XText(Environment.NewLine));

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    public static bool MatchesHelperFile(string? value, string normalizedFileName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value!.Replace('\\', '/');
        return normalized.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("/" + normalizedFileName, StringComparison.OrdinalIgnoreCase);
    }
}
