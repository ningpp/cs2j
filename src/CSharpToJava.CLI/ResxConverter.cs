using System.Text;
using System.Xml.Linq;

namespace CSharpToJava.CLI;

/// <summary>
/// Converts .NET .resx files to Java ResourceBundle .properties files.
/// C# SR.cs uses ResourceManager("RootNamespace.Resources.Strings") which the
/// compat layer backs with ResourceBundle.getBundle(baseName) — the bundle must
/// exist as &lt;baseName with dots as path&gt;.properties on the classpath. The
/// converter otherwise copies .resx verbatim, so every SR string fell back to
/// "{Name}" and JSON test data became invalid (e.g. "{FullJsonSchema1}").
/// </summary>
internal static class ResxConverter
{
    /// <summary>
    /// Parses a .resx file and returns Java .properties content.
    /// Keys are the &lt;data name&gt; values; values come from &lt;value&gt;.
    /// </summary>
    public static string ConvertToProperties(string resxPath)
    {
        var doc = XDocument.Load(resxPath);
        var sb = new StringBuilder();
        foreach (var data in doc.Descendants().Where(e => e.Name.LocalName == "data"))
        {
            var name = (string?)data.Attribute("name");
            var value = data.Elements().FirstOrDefault(e => e.Name.LocalName == "value")?.Value ?? "";
            if (string.IsNullOrEmpty(name))
                continue;
            sb.Append(EscapeKey(name)).Append('=').Append(EscapeValue(value)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Computes the embedded-resource logical name for a project resource:
    /// RootNamespace + relative path with dots, extension stripped
    /// (e.g. RootNamespace "dotnet.text.json" + "Resources/Strings.resx"
    /// → "dotnet.text.json.Resources.Strings").
    /// </summary>
    public static string ComputeLogicalName(string? rootNamespace, string relativePath)
    {
        var withoutExt = Path.ChangeExtension(relativePath, null);
        var dotted = withoutExt.Replace('\\', '.').Replace('/', '.');
        return string.IsNullOrEmpty(rootNamespace)
            ? dotted
            : rootNamespace + "." + dotted;
    }

    /// <summary>Reads RootNamespace from a .csproj (SDK projects), or null.</summary>
    public static string? ReadRootNamespace(string projectFilePath)
    {
        try
        {
            var doc = XDocument.Load(projectFilePath);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeKey(string key) => key.Replace(":", "\\:").Replace("=", "\\=");

    private static string EscapeValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20 || ch > 0x7E)
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
