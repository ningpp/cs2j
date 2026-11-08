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
