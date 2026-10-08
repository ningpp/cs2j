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
        var normalizedRelativeInclude = relativeInclude.Replace('\\', '/');

        // csproj-entry 布局下项目目录就是中间树根，生成文件落在项目目录内，
        // SDK 默认 glob 会自动 Compile 它；随后的显式 Include 即触发
        // NETSDK1022 重复项。sln 布局下相对路径以 `..` 开头（文件在项目目录
        // 外），默认 glob 不覆盖，无需 Remove。
        var needsGlobSuppression = !normalizedRelativeInclude
            .StartsWith("../", StringComparison.Ordinal);

        var content = new List<object> { new XText(Environment.NewLine + "  ") };
        if (needsGlobSuppression)
        {
            content.Add(new XElement(
                "ItemGroup",
                new XElement("Compile", new XAttribute("Remove", relativeInclude))));
            content.Add(new XText(Environment.NewLine + "  "));
        }

        content.Add(new XElement(
            "ItemGroup",
            new XElement(
                "Compile",
                new XAttribute("Include", relativeInclude),
                new XAttribute("Link", fileName))));
        content.Add(new XText(Environment.NewLine));
        root.Add(content.ToArray());

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
